import { EmbeddedSelect } from '../components/EmbeddedSelect';
import './BlueMoonPreferences.scss';

export type PhysiologyOption = { id: string; label: string; kind: string; value: number; min: number; max: number;
  options?: { id: number; name: string }[] };

export const BlueMoonPreferences = ({ options = [], act }: {
  options?: PhysiologyOption[]; act: (action: string, payload: object) => void;
}) => <div className="BlueMoonPreferences">
  {options.map(option => <label key={option.id}><span>{option.label}</span>
    {option.kind === 'toggle' ? <input type="checkbox" checked={!!option.value}
      onChange={e => act('physiology', { id: option.id, value: e.target.checked ? 1 : 0 })} />
      : option.kind === 'select' ? <EmbeddedSelect value={option.value}
        onChange={e => act('physiology', { id: option.id, value: Number(e.target.value) })}>
        {option.options?.map(value => <option key={value.id} value={value.id}>{value.name}</option>)}
      </EmbeddedSelect> : <input type="number" min={option.min} max={option.max} step={option.id.includes("multiplier") || option.id.endsWith("-rate") ? "0.1" : "1"} value={option.value}
        onChange={e => { const value = Number(e.target.value); if (e.target.value && Number.isFinite(value))
          act('physiology', { id: option.id, value: Math.max(option.min, Math.min(option.max, value)) }); }} />}
  </label>)}
</div>;
