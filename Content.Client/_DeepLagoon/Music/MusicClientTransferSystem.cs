using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Content.Shared._DeepLagoon.Music;
using Robust.Shared.Asynchronous;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Transfer;

namespace Content.Client._DeepLagoon.Music;

public sealed partial class MusicClientTransferSystem : EntitySystem
{
    [Dependency] private ILogManager _log = default!;
    [Dependency] private ITaskManager _tasks = default!;

    private MusicAudioSystem _audio = default!;
    private ISawmill _sawmill = default!;
    private bool _stopped;

    public override void Initialize()
    {
        base.Initialize();
        _stopped = false;
        _sawmill = _log.GetSawmill("dl.music.recv");
        _audio = EntityManager.System<MusicAudioSystem>();
    }

    public override void Shutdown()
    {
        _stopped = true;
        base.Shutdown();
    }

    public void OnTrack(TransferReceivedEvent ev)
    {
        _ = ReceiveAsync(ev);
    }

    private async Task ReceiveAsync(TransferReceivedEvent ev)
    {
        try
        {
            await using var stream = ev.DataStream;

            var lengthBytes = new byte[4];
            await stream.ReadExactlyAsync(lengthBytes);
            var idLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (idLength is <= 0 or > MusicConstants.MaxIdLength)
            {
                _sawmill.Warning("Music stream rejected: bad id length {Length}", idLength);
                return;
            }

            var idBytes = new byte[idLength];
            await stream.ReadExactlyAsync(idBytes);
            var id = Encoding.UTF8.GetString(idBytes);

            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
                buffer.Write(chunk, 0, read);

            var bytes = buffer.ToArray();

            _tasks.RunOnMainThread(() =>
            {
                if (_stopped) return;
                try
                {
                    _audio.Begin(id);
                    _audio.Append(id, bytes, bytes.Length);

                    if (!_audio.Complete(id))
                        _sawmill.Warning("Music track {Id} could not be decoded", id);
                }
                catch (Exception e)
                {
                    _sawmill.Warning("Music decode failed: {Error}", e.GetType().Name);
                }
            });
        }
        catch (Exception e)
        {
            _sawmill.Warning("Music receive failed: {Error}", e.GetType().Name);
        }
    }
}
