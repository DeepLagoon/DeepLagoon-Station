namespace Content.Server.Instruments;

public sealed partial class InstrumentSystem
{
    public void ConfigureTraitSinger(EntityUid uid, InstrumentComponent instrument, bool allInstruments)
    {
        SetInstrumentProgram(uid, instrument, 52, 0);
        instrument.AllowPercussion = allInstruments;
        instrument.AllowProgramChange = allInstruments;
        Dirty(uid, instrument);
    }
}
