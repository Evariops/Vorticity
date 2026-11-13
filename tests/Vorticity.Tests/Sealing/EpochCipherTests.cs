// The cipher of one epoch, rented by several lanes while it is disposed.
//
// WHAT IS HELD: a rent that a dispose overtakes either is refused or gets an instance over the
// epoch's key, never one over a key already wiped and freed, which would fail every frame as if the
// object were altered. More lanes than the machine has cores rent and drop instances as fast as they
// can, so that a lane is taken off its core in the middle of a rent, while one cipher after another
// is disposed under them at a varying moment. With no guard at all, hundreds of rents a run opened
// under a wiped key. With a flag and a count of the rents in two words, a few still did: a rent on its
// way out found the count at zero, another came in and passed the flag, the dispose then set the flag
// and left the key to that one, and the first, reading the flag at last, wiped the key under it.
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
        const int Rounds = 20_000;
        int laneCount = Math.Clamp(Environment.ProcessorCount * 2, 8, 32);
        byte[] key = SealedObjects.Key(7);
        byte[] plaintext = new byte[64];
        byte[] frame = new byte[64];
        byte[] tag = new byte[SealedFormat.TagBytes];
        using (AesGcm sealing = new AesGcm(key, SealedFormat.TagBytes))
        {
            EpochCipher.Seal(sealing, 0, final: true, plaintext, frame, tag);
        }

        EpochCipher current = new EpochCipher(key);
        bool done = false;
        int wrong = 0;
        long rents = 0;
        Thread[] lanes = new Thread[laneCount];
        for (int t = 0; t < lanes.Length; t++)
        {
            lanes[t] = new Thread(Lane);
            lanes[t].Start();
        }

        // The cipher the lanes rent from is disposed under them, then the next one takes its place.
        for (int round = 0; round < Rounds; round++)
        {
            Thread.SpinWait(100 + ((round % 64) * 16));
            Volatile.Read(ref current).Dispose();
            Volatile.Write(ref current, new EpochCipher(key));
        }

        Volatile.Write(ref done, true);
        foreach (Thread lane in lanes)
        {
            lane.Join();
        }

        current.Dispose();
        Assert.True(wrong == 0, $"{wrong} rents of {rents:N0} opened a frame under a wiped key");
        Assert.True(rents > Rounds, $"the lanes rented {rents:N0} instances over {Rounds:N0} ciphers, too few to meet a dispose");

        // Each lane drops what it rents rather than returning it, so every rent makes an instance from the key.
        void Lane()
        {
            byte[] opened = new byte[64];
            long rented = 0;
            while (!Volatile.Read(ref done))
            {
                AesGcm aes;
                try
                {
                    aes = Volatile.Read(ref current).Rent();
                }
                catch (ObjectDisposedException)
                {
                    // Refused: the next cipher is on its way, which the round's thread needs a core to bring.
                    Thread.Yield();
                    continue;
                }

                rented++;
                try
                {
                    EpochCipher.Open(aes, 0, final: true, frame, tag, opened, 0);
                }
                catch (Exception)
                {
                    // Counted rather than thrown: an exception a lane lets out ends the test's process.
                    Interlocked.Increment(ref wrong);
                }
                finally
                {
                    aes.Dispose();
                }
            }

            Interlocked.Add(ref rents, rented);
        }
    }
}
