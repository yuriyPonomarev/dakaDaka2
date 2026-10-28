using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class NetEndpointTests
    {
        [TestCase("", "127.0.0.1", 7777)]
        [TestCase("   ", "127.0.0.1", 7777)]
        [TestCase("127.0.0.1:7777", "127.0.0.1", 7777)]
        [TestCase("192.168.1.20", "192.168.1.20", 7777)]
        [TestCase("192.168.1.20:9000", "192.168.1.20", 9000)]
        [TestCase(" 10.0.0.5 : 65535 ", "10.0.0.5", 65535)]
        [TestCase(":8123", "127.0.0.1", 8123)]
        [TestCase("localhost", "127.0.0.1", 7777)]
        [TestCase("LocalHost:7000", "127.0.0.1", 7000)]
        [TestCase("010.000.000.001:1", "10.0.0.1", 1)]
        public void Parse_Valid(string text, string address, int port)
        {
            Assert.That(NetEndpoint.TryParse(text, out string a, out int p, out string error), Is.True, error);
            Assert.That(a, Is.EqualTo(address));
            Assert.That(p, Is.EqualTo(port));
            Assert.That(error, Is.EqualTo(""));
        }

        [TestCase("1.2.3")]
        [TestCase("1.2.3.4.5")]
        [TestCase("256.1.1.1")]
        [TestCase("a.b.c.d")]
        [TestCase("example.com")]
        [TestCase("1.2.3.4:0")]
        [TestCase("1.2.3.4:65536")]
        [TestCase("1.2.3.4:abc")]
        [TestCase("1.2.3.4:")]
        [TestCase("1.2.3.4:-5")]
        [TestCase("::1")]
        [TestCase("1.2.3.4:5:6")]
        [TestCase("1.2.3.-4")]
        [TestCase("1..3.4")]
        public void Parse_Malformed_IsRefused(string text)
        {
            Assert.That(NetEndpoint.TryParse(text, out string a, out int p, out string error), Is.False, text);
            Assert.That(error, Is.Not.Empty);
            Assert.That(a, Is.EqualTo(NetTuning.DefaultAddress));
            Assert.That(p, Is.EqualTo(NetTuning.DefaultPort));
        }

        [Test]
        public void Parse_TooLong_IsRefused()
        {
            Assert.That(NetEndpoint.TryParse(new string('1', NetEndpoint.MaxLength + 1), out _, out _, out string error), Is.False);
            Assert.That(error, Does.Contain("too long"));
        }
    }
}
