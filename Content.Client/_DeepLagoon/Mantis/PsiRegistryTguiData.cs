using Content.Client._DeepLagoon.WebUI;
using Content.Shared._DeepLagoon.Mantis;
using Content.Shared.Psionics;

namespace Content.Client._DeepLagoon.Mantis;

public static class PsiRegistryTguiData
{
    public static TguiData Build(PsiRegistryUiState state)
    {
        var records = new List<TguiData>();

        foreach (var record in state.Records)
        {
            records.Add(new TguiData()
                .Number("key", record.Key)
                .String("name", record.Name)
                .String("job", record.Job.Length == 0 ? Loc.GetString("psi-registry-unknown-job") : record.Job)
                .String("status", Status(record.Status))
                .String("reason", record.Reason ?? Loc.GetString("psi-registry-reason-none"))
                .Bool("selected", record.Key == state.SelectedKey));
        }

        var data = new TguiData()
            .Object("labels", Labels())
            .Bool("canEdit", state.CanEdit)
            .Bool("hasSelection", state.SelectedKey != null)
            .Array("records", records);

        if (state.SelectedKey is { } key)
        {
            foreach (var record in state.Records)
            {
                if (record.Key != key)
                    continue;

                data.Number("selectedKey", record.Key)
                    .String("selectedName", record.Name)
                    .String("selectedJob", record.Job.Length == 0 ? Loc.GetString("psi-registry-unknown-job") : record.Job)
                    .String("selectedStatus", Status(record.Status))
                    .String("selectedReason", record.Reason ?? Loc.GetString("psi-registry-reason-none"));
                break;
            }
        }

        return data;
    }

    public static TguiData Labels() => new TguiData()
        .String("title", Loc.GetString("psi-registry-header"))
        .String("list", Loc.GetString("psi-registry-list-title"))
        .String("empty", Loc.GetString("psi-registry-no-records"))
        .String("selectRecord", Loc.GetString("psi-registry-select-record"))
        .String("reasonLabel", Loc.GetString("psi-registry-reason-label"))
        .String("reasonPlaceholder", Loc.GetString("psi-registry-reason-placeholder"))
        .String("setStatus", Loc.GetString("psi-registry-set-status"))
        .String("reasonRequired", Loc.GetString("psi-registry-reason-required"))
        .String("accessGranted", Loc.GetString("psi-registry-access-granted"))
        .String("accessDenied", Loc.GetString("psi-registry-access-denied"))
        .String("job", Loc.GetString("psi-registry-column-job"))
        .String("status", Loc.GetString("psi-registry-column-status"))
        .String("reason", Loc.GetString("psi-registry-column-reason"))
        .String("clean", Loc.GetString("psi-registry-status-registered-short"))
        .String("suspected", Loc.GetString("psi-registry-status-suspected-short"))
        .String("abusing", Loc.GetString("psi-registry-status-abusing-short"))
        .String("reset", Loc.GetString("psi-registry-status-none-short"));

    public static string Status(PsionicsStatus status) =>
        Loc.GetString("psi-registry-status-" + status.ToString().ToLowerInvariant());
}
