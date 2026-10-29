using System.Globalization;
using System.Text;
using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mapping;

namespace Content.Client._DeepLagoon.Mapping;

public static class GridEditorJson
{
    public static void AppendItems(StringBuilder builder, IEnumerable<GridEditorItem> items)
    {
        var separator = false;
        foreach (var item in items)
        {
            if (separator) builder.Append(',');
            separator = true;
            builder.Append("{\"id\":"); GameWebView.AppendQuoted(builder, item.Id);
            builder.Append(",\"kind\":"); GameWebView.AppendQuoted(builder, item.Kind);
            builder.Append(",\"prototype\":"); GameWebView.AppendQuoted(builder, item.Prototype);
            builder.Append(",\"x\":").Append(Number(item.X)).Append(",\"y\":").Append(Number(item.Y));
            builder.Append(",\"rotation\":").Append(Number(item.Rotation));
            builder.Append(",\"color\":"); GameWebView.AppendQuoted(builder, item.Color);
            builder.Append(",\"z\":").Append(item.Z.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"cleanable\":").Append(item.Cleanable ? "true" : "false");
            builder.Append(",\"anchored\":").Append(item.Anchored ? "true" : "false");
            builder.Append(",\"variant\":").Append(item.Variant.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"flags\":").Append(item.Flags.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"mirror\":").Append(item.Mirror.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"pipeLayer\":").Append(item.PipeLayer.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"entityData\":"); GameWebView.AppendQuoted(builder, item.EntityData);
            builder.Append(",\"entityGroup\":"); GameWebView.AppendQuoted(builder, item.EntityGroup);
            if (item.GasPorts != 0) builder.Append(",\"gasPorts\":").Append(item.GasPorts.ToString(CultureInfo.InvariantCulture));
            builder.Append('}');
        }
    }

    private static string Number(float value) => (float.IsFinite(value) ? value : 0).ToString(CultureInfo.InvariantCulture);
}
