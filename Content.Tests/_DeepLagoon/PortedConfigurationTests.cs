using System;
using System.Linq;
using System.Reflection;
using Content.Shared._Exodus.CCVar;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.UnitTesting;

namespace Content.Tests._DeepLagoon;

[TestFixture(UnitTestProject.Server)]
[TestFixture(UnitTestProject.Client)]
public sealed class PortedConfigurationTests(UnitTestProject project) : RobustUnitTest
{
    public override UnitTestProject Project => project;

    protected override Assembly[] GetContentAssemblies() => [typeof(EXCVars).Assembly];

    [Test]
    public void EveryContentCVarDeclarationCanBeDiscovered()
    {
        var assemblies = new[] { typeof(EXCVars).Assembly, typeof(Content.Client.Entry.EntryPoint).Assembly,
            typeof(Content.Server.Entry.EntryPoint).Assembly };
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(field => typeof(CVarDef).IsAssignableFrom(field.FieldType)).ToArray();
            if (fields.Length == 0)
                continue;
            Assert.That(Attribute.IsDefined(type, typeof(CVarDefsAttribute)), Is.True, type.FullName);
            foreach (var field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, $"{type.FullName}.{field.Name}");
                Assert.That(field.GetValue(null), Is.Not.Null, $"{type.FullName}.{field.Name}");
            }
        }
    }

    [Test]
    public void AssemblyDiscoveryRegistersPortedSettingsForCorrectSide()
    {
        var config = IoCManager.Resolve<IConfigurationManager>();
        if (Project == UnitTestProject.Server)
        {
            Assert.That(config.GetCVar(EXCVars.ParallelMoverUpdate), Is.True);
            Assert.That(config.GetCVar(EXCVars.ParallelProjectileRaycasts), Is.True);
            Assert.That(config.IsCVarRegistered(EXCVars.DecalFavoriteColors.Name), Is.False);
            Assert.That(config.IsCVarRegistered(EXCVars.RadarGridRebuildInterval.Name), Is.False);
        }
        else
        {
            Assert.That(config.GetCVar(EXCVars.DecalFavoriteColors), Is.Empty);
            Assert.That(config.GetCVar(EXCVars.RadarGridRebuildInterval), Is.EqualTo(0.1f));
            Assert.That(config.IsCVarRegistered(EXCVars.ParallelMoverUpdate.Name), Is.False);
            Assert.That(config.IsCVarRegistered(EXCVars.ParallelProjectileRaycasts.Name), Is.False);
        }
    }
}
