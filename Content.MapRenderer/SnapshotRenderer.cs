using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Content.MapRenderer.Painters;
using Newtonsoft.Json;
using SixLabors.ImageSharp;

namespace Content.MapRenderer;

internal static class SnapshotRenderer
{
    public static async Task Render(string path, string output)
    {
        Directory.CreateDirectory(output);
        var grids = new List<object>();
        await foreach (var grid in MapPainter.PaintSnapshot(Path.GetFullPath(path)))
        {
            var file = $"grid-{grids.Count}.png";
            await grid.Image.SaveAsPngAsync(Path.Combine(output, file));
            grids.Add(new
            {
                file,
                yaml_uid = grid.YamlUid,
                x = grid.LocalOrigin.X,
                y = grid.LocalOrigin.Y,
                width = grid.Image.Width,
                height = grid.Image.Height,
            });
            grid.Image.Dispose();
        }
        if (grids.Count == 0)
            throw new InvalidDataException("Snapshot contained no renderable grids");
        // This is the completion marker: partial PNG output must never count as a successful render.
        await File.WriteAllTextAsync(Path.Combine(output, "snapshot.json"), JsonConvert.SerializeObject(grids));
    }
}
