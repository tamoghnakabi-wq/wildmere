using System;
using System.Text;
using NUnit.Framework;
using Wildmere.Core;

namespace Wildmere.Tests
{
    /// <summary>Auto-update: version ordering and the manifest signature check against the compiled-in key.</summary>
    public class UpdaterTests
    {
        // Signed with .secrets/wildmere-update-key.pem (openssl dgst -sha256 -sign), so a pass also proves the
        // public key in Updater.cs matches the release signing key.
        const string kVector = "{\"schema\":1,\"version\":\"9.9.9\",\"note\":\"editmode test vector\"}";
        const string kVectorSig = "Nhak+ZsHwzi0UW/wZWPiLthPDoj3CDYGJpCo+mT2mnDYV+mSrq7xkJXSjK9F9jERXGFHAb+Mou/Fm6cMcAbwFBpjBqYxcPpIwLZBMkbrHN2UDsOUFu59dYHD5eHVa0vC/FvoQ8EyD5DnsUR+n1lVtg4qrlf94xfCVSJqU3xu5UEhmrd5WWOAz1AeXwjYZ2a9gqFuzTdhVnt524X58w754TCQLSAFVMSeaP0ZxFmlp+LwOfbas+s2Y009i3Il3w9jJCVmqwudKdIcX/q1KLjxtexM9k8o+OcxvegmqYmBVEPbeBYZ/RIKiYZBS9XFjZAcIAjZ65ErscDW8/9d+VN0+4jsZyvmAe66xgZwTPkO0iQ/bxX8B1Ry1gAm3LWWJ277vcBuczA2BFztep+yIPwp0YcekM1RmxFqM41ik+h1X114VfYfKOUIhUNNscNzAn+dlA+jZqgFd7kNL7etj/6qItwXNTEcTXdMDqpA4wPNwR4mZKyGWN40Au6wE89T9SpT";

        [TestCase("0.1.3", "0.1.2", 1)]
        [TestCase("0.1.10", "0.1.9", 1)]
        [TestCase("0.2.0", "0.1.99", 1)]
        [TestCase("1.0.0", "0.9.9", 1)]
        [TestCase("0.1.3", "0.1.3", 0)]
        [TestCase("v0.1.3", "0.1.3", 0)]
        [TestCase("0.1.3-rc.2", "0.1.3", 0)]
        [TestCase("0.1.2", "0.1.3", -1)]
        [TestCase("0.1", "0.1.0", 0)]
        public void CompareVersions_IsNumeric(string a, string b, int expected)
        {
            Assert.AreEqual(expected, Math.Sign(Updater.CompareVersions(a, b)));
        }

        [Test]
        public void Signature_FromReleaseKey_Verifies()
        {
            Assert.IsTrue(Updater.VerifySignature(Encoding.UTF8.GetBytes(kVector), Convert.FromBase64String(kVectorSig)));
        }

        [Test]
        public void Signature_RejectsTamperedData()
        {
            var data = Encoding.UTF8.GetBytes(kVector.Replace("9.9.9", "9.9.8"));
            Assert.IsFalse(Updater.VerifySignature(data, Convert.FromBase64String(kVectorSig)));
        }

        [Test]
        public void Signature_RejectsDamagedOrMissingSignature()
        {
            var sig = Convert.FromBase64String(kVectorSig);
            sig[10] ^= 0x40;
            Assert.IsFalse(Updater.VerifySignature(Encoding.UTF8.GetBytes(kVector), sig));
            Assert.IsFalse(Updater.VerifySignature(Encoding.UTF8.GetBytes(kVector), new byte[0]));
        }
    }
}
