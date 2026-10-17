import { useBackend } from '../backend';
import { CivilianConsoleFrame, CivilianConsoleButton as Button } from '../components/CivilianConsoleFrame';
import './DiskConsole.scss';

export const DiskConsoleScreen = ({ data, act, layout = 'compact' }) => (
  <CivilianConsoleFrame className="DiskConsole" data-layout={layout}>
    <header className="DiskConsole__header">
      <h1>{data.title}</h1>
      <Button onClick={() => act('server')}>{data.serverLabel}</Button>
      <Button aria-label={data.closeLabel} onClick={() => act('close')}>×</Button>
    </header>
    <main className="DiskConsole__body">
      <section className="DiskConsole__balance" aria-live="polite">
        <span>{data.totalLabel || data.waitingLabel}</span>
      </section>
      <div className="DiskConsole__operations">
        {(data.entries || []).map(entry => <Button key={entry.id} disabled={!data.ready || entry.disabled}
          className="DiskConsole__operation" onClick={() => act('print', { id: entry.id })}>
          <span className="DiskConsole__disk" aria-hidden="true" />
          <span>{entry.label}</span>
        </Button>)}
      </div>
    </main>
  </CivilianConsoleFrame>
);

export const DiskConsole = () => {
  const { data, act } = useBackend();
  return <DiskConsoleScreen data={data} act={act} layout="ledger" />;
};
