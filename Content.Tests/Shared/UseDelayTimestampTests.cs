using System;
using System.Reflection;
using Content.Shared.Timing;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.UnitTesting;

namespace Content.Tests.Shared;

[TestFixture]
public sealed class UseDelayTimestampTests : RobustUnitTest
{
    protected override Assembly[] GetContentAssemblies() => [typeof(UseDelayInfo).Assembly];

    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    [Test]
    public void LegacyAbsoluteCooldownsExpireWithoutChangingTheirDuration()
    {
        var manager = IoCManager.Resolve<ISerializationManager>();
        var restored = manager.Read<UseDelayInfo>(new MappingDataNode
        {
            { "length", "1" }, { "startTime", "36000" }, { "endTime", "36001" },
        });
        Assert.That(restored.Length, Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(restored.StartTime, Is.EqualTo(TimeSpan.Zero));
        Assert.That(restored.EndTime, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void NewFormatIsMarkedAndDoesNotPersistPrototypeOrUninitializedClocks()
    {
        var manager = IoCManager.Resolve<ISerializationManager>();
        var serializer = new UseDelayTimestampSerializer();
        var written = (ValueDataNode) serializer.Write(manager, TimeSpan.FromHours(10), IoCManager.Instance!);
        Assert.That(written.Value, Is.EqualTo("offset:0"));
        Assert.That(serializer.Read(manager, written, IoCManager.Instance!, default), Is.EqualTo(TimeSpan.Zero));
        Assert.That(serializer.Validate(manager, new ValueDataNode("offset:-1.25"), IoCManager.Instance!), Is.TypeOf<ValidatedValueNode>());
        Assert.That(serializer.Validate(manager, new ValueDataNode("36001"), IoCManager.Instance!), Is.TypeOf<ValidatedValueNode>());
        Assert.That(serializer.Validate(manager, new ValueDataNode("offset:invalid"), IoCManager.Instance!), Is.TypeOf<ErrorNode>());
        var runtime = new UseDelayInfo(TimeSpan.FromSeconds(1), TimeSpan.FromHours(10), TimeSpan.FromHours(10) + TimeSpan.FromSeconds(1));
        var copy = manager.CreateCopy(runtime);
        Assert.That(copy.StartTime, Is.EqualTo(runtime.StartTime), "In-memory copying must retain the absolute runtime clock.");
        Assert.That(copy.EndTime, Is.EqualTo(runtime.EndTime));
    }
}
