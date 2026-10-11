// The reader of the services' XML: what S3 writes in a listing and an error, its entities and CDATA,
// and a fuzz that holds it to one promise, that no input escapes it but as an ObjectStoreException.
using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.IO;
using Vorticity.ObjectStorage.S3;
using Vorticity.ObjectStorage.Xml;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class XmlScannerTests
{
    private const string Listing = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
          <Name>lake</Name><Prefix>data%2F</Prefix><KeyCount>2</KeyCount><MaxKeys>2</MaxKeys>
          <EncodingType>url</EncodingType><IsTruncated>true</IsTruncated>
          <Contents>
            <Key>data/a+b%26c.vortex</Key><LastModified>2026-10-11T08:30:00.000Z</LastModified>
            <ETag>&quot;9b2cf535f27731c974343645a3985328&quot;</ETag><ChecksumAlgorithm>CRC64NVME</ChecksumAlgorithm>
            <Size>434234</Size><Owner><ID>x</ID></Owner><StorageClass>STANDARD</StorageClass>
          </Contents>
          <Contents><Key>data/%C3%A9t%C3%A9</Key><LastModified>2026-10-11T08:31:00.000Z</LastModified><ETag>"e"</ETag><Size>0</Size></Contents>
          <NextContinuationToken>1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM=</NextContinuationToken>
        </ListBucketResult>
        """;

    [Fact]
    public void AListingGivesEachKeyDecodedWithItsHeadAndTheNextToken()
    {
        List<ObjectListing> page = [];
        string? next = S3Answers.ParseListing(Encoding.UTF8.GetBytes(Listing), 0, lockReported: false, page);
        Assert.Equal("1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM=", next);
        Assert.Equal(2, page.Count);
        Assert.Equal("data/a b&c.vortex", page[0].Key);
        Assert.Equal(new ObjectHead(434_234, "\"9b2cf535f27731c974343645a3985328\"", new DateTimeOffset(2026, 10, 11, 8, 30, 0, TimeSpan.Zero)), page[0].Head);
        Assert.False(page[0].LockReported);
        Assert.Equal(("data/été", 0L), (page[1].Key, page[1].Head.Length));

        // A prefix the store strips.
        page.Clear();
        S3Answers.ParseListing(Encoding.UTF8.GetBytes(Listing), "data/".Length, lockReported: true, page);
        Assert.Equal(["a b&c.vortex", "été"], page.ConvertAll(o => o.Key));
    }

    [Fact]
    public void AnErrorGivesItsCodeMessageRequestIdAndSize()
    {
        const string error = "<Error><Code>InvalidRange</Code><Message>The requested range is not satisfiable</Message>" +
            "<RangeRequested>bytes=10-20</RangeRequested><ActualObjectSize>10</ActualObjectSize><RequestId>4442587FB7D0A2F9</RequestId></Error>";
        Assert.Equal(("InvalidRange", "The requested range is not satisfiable", "4442587FB7D0A2F9", 10L), S3Answers.ParseError(Encoding.UTF8.GetBytes(error)));
    }

    [Theory]
    [InlineData("<a>x &lt;&gt;&amp;&quot;&apos; y</a>", "x <>&\"' y")]
    [InlineData("<a>&#233;&#x20AC;&#x1F600;</a>", "é€😀")]
    [InlineData("<a><![CDATA[<raw> &amp;]]></a>", "<raw> &amp;")]
    [InlineData("<a>one<!-- skipped -->two</a>", "onetwo")]
    [InlineData("<a/>", "")]
    [InlineData("<a attr=\"x>y\" other='z'>v</a>", "v")]
    [InlineData("﻿<a>bom</a>", "bom")]
    public void TextDecodesItsEntitiesAndKeepsCData(string xml, string text)
    {
        XmlScanner scanner = new XmlScanner(Encoding.UTF8.GetBytes(xml));
        Assert.True(scanner.Read());
        Assert.Equal(XmlToken.Start, scanner.Token);
        Assert.Equal(text, scanner.ReadText());
    }

    [Theory]
    [InlineData("<!DOCTYPE a [<!ENTITY x \"boom\">]><a>&x;</a>")]
    [InlineData("<a>&unknown;</a>")]
    [InlineData("<a>&#0;</a>")]
    [InlineData("<a>&#xD800;</a>")]
    [InlineData("<a>&amp</a>")]
    [InlineData("<a><b>nested</b></a>")]
    [InlineData("<a>unterminated")]
    [InlineData("<a")]
    [InlineData("<![CDATA[open")]
    [InlineData("<a>ÿþ</a>")]
    public void WhatIsNotWellFormedIsRefusedAsTheStoresException(string xml)
    {
        byte[] bytes = xml.Contains('ÿ', StringComparison.Ordinal) ? [(byte)'<', (byte)'a', (byte)'>', 0xFF, 0xFE, (byte)'<', (byte)'/', (byte)'a', (byte)'>'] : Encoding.UTF8.GetBytes(xml);
        Assert.Throws<ObjectStoreException>(() =>
        {
            XmlScanner scanner = new XmlScanner(bytes);
            while (scanner.Read())
            {
                if (scanner.Token == XmlToken.Start)
                {
                    scanner.ReadText();
                }
            }
        });
    }

    [Fact]
    public void NoInputEscapesButAsTheStoresException()
    {
        // Mutations of a real listing and a real error: bytes flipped, cut, duplicated, spliced.
        byte[][] seeds = [Encoding.UTF8.GetBytes(Listing), Encoding.UTF8.GetBytes("<Error><Code>SlowDown</Code><Message>Reduce your request rate.</Message></Error>")];
        Random random = new Random(0x0F_A2);
        for (int round = 0; round < 20_000; round++)
        {
            byte[] seed = seeds[round % seeds.Length];
            byte[] input = Mutate(seed, random);
            try
            {
                List<ObjectListing> page = [];
                S3Answers.ParseListing(input, 0, false, page);
            }
            catch (ObjectStoreException)
            {
            }

            try
            {
                S3Answers.ParseError(input);
            }
            catch (ObjectStoreException)
            {
            }
        }
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        byte[] bytes = (byte[])seed.Clone();
        switch (random.Next(4))
        {
            case 0:
                for (int i = random.Next(1, 8); i > 0; i--)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }

                return bytes;
            case 1:
                return bytes[..random.Next(bytes.Length)];
            case 2:
                int at = random.Next(bytes.Length);
                return [.. bytes[..at], .. "<&;!-[]>\"'"u8.ToArray().AsSpan(random.Next(10)).ToArray(), .. bytes[at..]];
            default:
                int from = random.Next(bytes.Length);
                int length = random.Next(bytes.Length - from);
                return [.. bytes, .. bytes.AsSpan(from, length).ToArray()];
        }
    }
}
