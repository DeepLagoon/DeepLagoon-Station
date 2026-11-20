using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Components;
using Moq;
using NUnit.Framework;
using Robust.Shared.Random;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class DeviceNetAddressTests
{
    [Test, Timeout(5000)]
    public void RepeatedOccupiedRandomAddressDoesNotHangOrOverwriteDevices()
    {
        var random = new Mock<IRobustRandom>();
        random.Setup(r => r.Next()).Returns(0);
        var network = new DeviceNet(1, random.Object);
        for (var i = 0; i < 1024; i++)
        {
            var device = new DeviceNetworkComponent();
            Assert.That(network.Add(device), Is.True);
            Assert.That(network.Devices[device.Address], Is.SameAs(device));
        }
        Assert.That(network.Devices.Count, Is.EqualTo(1024));
        random.Verify(r => r.Next(), Times.Exactly(1 + 16 * 1023));
    }

    [Test, Timeout(5000)]
    public void FallbackSkipsExistingCustomAddresses()
    {
        var random = new Mock<IRobustRandom>();
        random.Setup(r => r.Next()).Returns(0);
        var network = new DeviceNet(1, random.Object);
        for (var i = 1; i <= 128; i++)
        {
            var reserved = new DeviceNetworkComponent();
            Assert.That(network.Add(reserved), Is.True);
            Assert.That(network.UpdateAddress(reserved.Address, $"0000-{i:X4}"), Is.True);
            Assert.That(reserved.CustomAddress, Is.True);
        }
        Assert.That(network.Add(new DeviceNetworkComponent()), Is.True); // Occupy random address zero too.
        var device = new DeviceNetworkComponent();
        Assert.That(network.Add(device), Is.True);
        Assert.That(device.Address, Is.EqualTo("0000-0081"));
        Assert.That(network.Devices.Count, Is.EqualTo(130));
    }

    [Test]
    public void UnoccupiedRandomAddressKeepsItsOriginalFormat()
    {
        var random = new Mock<IRobustRandom>();
        random.Setup(r => r.Next()).Returns(0x12345678);
        var network = new DeviceNet(1, random.Object);
        var device = new DeviceNetworkComponent();
        Assert.That(network.Add(device), Is.True);
        Assert.That(device.Address, Is.EqualTo("1234-5678"));
        random.Verify(r => r.Next(), Times.Once);
    }
}
