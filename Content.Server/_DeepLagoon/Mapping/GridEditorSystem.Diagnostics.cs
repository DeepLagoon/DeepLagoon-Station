using System.IO;
using System.Numerics;
using Content.Shared._DeepLagoon.Mapping;
using Robust.Shared.EntitySerialization;
using Robust.Shared.Map.Events;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private sealed class ExportException(GridEditorExportIssue issue, Exception inner) : Exception(issue.Message, inner)
    {
        public readonly GridEditorExportIssue Issue = issue;
    }

    private void SerializeForExport(EntitySerializer serializer, HashSet<EntityUid> roots, EntityUid editorGrid)
    {
        try
        {
            serializer.SerializeEntityRecursive(roots);
        }
        catch (Exception e)
        {
            var issue = new GridEditorExportIssue { Component = serializer.CurrentComponent ?? "", Message = e.GetBaseException().Message };
            if (serializer.CurrentEntity is { } entity && TryComp(entity.Owner, out TransformComponent? transform))
            {
                issue.Id = $"e:{GetNetEntity(entity.Owner)}";
                issue.Name = entity.Comp.EntityName;
                issue.Prototype = entity.Comp.EntityPrototype?.ID ?? "";
                issue.Located = transform.GridUid == editorGrid || entity.Owner == editorGrid;
                if (issue.Located)
                {
                    var position = Vector2.Transform(_transform.GetWorldPosition(entity.Owner), _transform.GetInvWorldMatrix(editorGrid));
                    issue.X = position.X;
                    issue.Y = position.Y;
                    issue.Located = float.IsFinite(issue.X) && float.IsFinite(issue.Y);
                }
            }
            var location = issue.Located ? $" ({issue.X:0.##}, {issue.Y:0.##})" : " (вне текущего grid или без координат)";
            issue.Message = $"{issue.Name} [{issue.Prototype}] {issue.Id}{location}, компонент {issue.Component}: {issue.Message}";
            throw new ExportException(issue, e);
        }
    }

    private string SerializeNativeExport(EntityUid root, EntityUid editorGrid, FileCategory category)
    {
        HashSet<EntityUid> roots = [root];
        RaiseLocalEvent(new BeforeSerializationEvent(roots, [Transform(root).MapID], category));
        var serializer = new EntitySerializer(_exportDependencies, new SerializationOptions { Category = category, ExpectPreInit = false });
        SerializeForExport(serializer, roots, editorGrid);
        var data = serializer.Write();
        var actualCategory = serializer.GetCategory();
        if (actualCategory != category)
            throw new InvalidDataException($"Ожидался экспорт {category}, получен {actualCategory}.");
        RaiseLocalEvent(new AfterSerializationEvent(roots, data, actualCategory));
        return WriteExport(data);
    }

    private static string WriteExport(MappingDataNode data)
    {
        using var writer = new StringWriter();
        new YamlStream { new YamlDocument(data.ToYaml()) }.Save(new YamlMappingFix(new Emitter(writer)), false);
        return writer.ToString();
    }
}
