// The secrets' memory (SecretBytes, SecretPages): a key lies in a cell of a page of the pool, which never moves; a key is a
// copy, gone once disposed; a cell given back is wiped before another key takes it; a key longer than a cell is refused.
using System;
using System.Linq;
using Vorticity.Sealing;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SecretPagesTests
{
    [Fact]
    public void AKeyIsACopyGoneOnceDisposed()
    {
        byte[] key = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
        SecretBytes[] secrets = [.. Enumerable.Range(0, 4).Select(_ => new SecretBytes(key))];
        Assert.All(secrets, secret => Assert.True(secret.Span.SequenceEqual(key)));
        key[0] = 0;
        Assert.All(secrets, secret => Assert.Equal(1, secret.Span[0]));

        foreach (SecretBytes secret in secrets)
        {
            secret.Dispose();
            Assert.Throws<ObjectDisposedException>(() => secret.Span.Length);
            secret.Dispose();
        }
    }

    /// <summary>
    /// A cell given back is wiped at once, while its page holds another key and stays: its bytes are gone, whether it lies
    /// wiped or another test's key took it meanwhile.
    /// </summary>
    [Fact]
    public void ACellGivenBackIsWiped()
    {
        (SecretPages.Page page, int cell) = SecretPages.Take();
        (SecretPages.Page other, int beside) = SecretPages.Take();
        try
        {
            page.Cell(cell).Fill(0xA5);
            SecretPages.Give(page, cell);
            if (ReferenceEquals(page, other))
            {
                Assert.True(page.Cell(cell).IndexOfAnyExcept((byte)0xA5) >= 0, "a key given back left its bytes in its cell");
            }
        }
        finally
        {
            SecretPages.Give(other, beside);
        }
    }

    [Fact]
    public void AKeyLongerThanACellIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SecretBytes(new byte[SecretPages.CellBytes + 1]));

    [Fact]
    public void AnEmptySecretTakesACellAndGivesItBack()
    {
        SecretBytes empty = new SecretBytes([]);
        Assert.Equal(0, empty.Span.Length);
        empty.Dispose();
        Assert.Throws<ObjectDisposedException>(() => empty.Span.Length);
    }
}
