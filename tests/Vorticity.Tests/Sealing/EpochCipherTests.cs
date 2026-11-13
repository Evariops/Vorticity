// The cipher of one epoch, rented by several lanes while it is disposed.
//
// WHAT IS HELD: a rent that a dispose overtakes either is refused or gets an instance over the
// epoch's key, never one over a key already wiped and freed, which would fail every frame as if the
// object were altered. Lanes rent and drop instances as fast as they can while the cipher is
// disposed at a varying moment, round after round: without the guard, some 250 rents of these
// 2,000 rounds opened under a wiped key.
using System;
using System.Security.Cryptography;
using System.Threading;
using Vorticity.Sealing;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class EpochCipherTests
{
    [Fact]
    public void ARentThatADisposeOvertakesNeverOpensWithAWipedKey()
    {
        const int Rounds = 2_000;
        const int Lanes = 4;
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] key = SealedObjects.Key(7);
        byte[] plaintext = new byte[64];
        byte[] frame = new byte[64];
        byte[] tag = new byte[SealedFormat.TagBytes];
        using (AesGcm sealing = new AesGcm(key, SealedFormat.TagBytes))
        {
            EpochCipher.Seal(sealing, 0, final: true, plaintext, frame, tag);
        }

        EpochCipher? current = null;
        int wrong = 0;
        using Barrier start = new Barrier(Lanes + 1);
        using Barrier end = new Barrier(Lanes + 1);
        Thread[] lanes = new Thread[Lanes];
        for (int t = 0; t < Lanes; t++)
        {
            lanes[t] = new Thread(Lane);
            lanes[t].Start();
        }

        for (int round = 0; round < Rounds; round++)
        {
            EpochCipher cipher = new EpochCipher(key);
            Volatile.Write(ref current, cipher);
            start.SignalAndWait(ct);
            Thread.SpinWait(200 + ((round % 50) * 20));
            cipher.Dispose();
            end.SignalAndWait(ct);
        }

        foreach (Thread lane in lanes)
        {
            lane.Join();
        }

        Assert.True(wrong == 0, $"{wrong} rents opened a frame under a wiped key");

        // Each lane drops what it rents rather than returning it, so every rent makes an instance from the key.
        void Lane()
        {
            byte[] opened = new byte[64];
            for (int round = 0; round < Rounds; round++)
            {
                start.SignalAndWait(ct);
                EpochCipher cipher = Volatile.Read(ref current)!;
                while (true)
                {
                    AesGcm aes;
                    try
                    {
                        aes = cipher.Rent();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    try
                    {
                        EpochCipher.Open(aes, 0, final: true, frame, tag, opened, 0);
                    }
                    catch (Exception)
                    {
                        // Counted rather than thrown: a lane that dies leaves the others waiting at the barrier.
                        Interlocked.Increment(ref wrong);
                    }
                    finally
                    {
                        aes.Dispose();
                    }
                }

                end.SignalAndWait(ct);
            }
        }
    }
}
