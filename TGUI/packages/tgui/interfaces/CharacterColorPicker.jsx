import { useState } from 'react';
import { useBackend } from '../backend';
import { Button } from '../components';
import { PlayerColorPicker } from '../components/PlayerColorPicker';
import { playerTheme } from '../components/PlayerTheme';
import { editorAppearance } from './CharacterEditorSettings';
import './CharacterEditor.scss';

export const CharacterColorPicker = () => {
  const { data, act } = useBackend();
  const [mode, setMode] = useState('wheel');
  const theme = playerTheme(data.appearance);
  return <div className={'CharacterColorDialog ' + theme.className}
    role="dialog" aria-label={data.title || 'Выбор цвета'}
    onKeyDown={event => { if (event.key === 'Escape') act('color-picker-close', { id: data.id }); }}
    style={{ ...theme.style, ...editorAppearance(data.appearance || {}) }}>
    <header><strong>{data.title || 'Выбор цвета'}</strong>
      <Button role="button" aria-label="Закрыть выбор цвета" onClick={() => act('color-picker-close', { id: data.id })}>×</Button>
    </header>
    <main>
      <div className="CharacterColorDialog__modes">
        <Button role="button" selected={mode === 'wheel'} onClick={() => setMode('wheel')}>Цветовой круг</Button>
        <Button role="button" selected={mode === 'hsv'} onClick={() => setMode('hsv')}>HSV</Button>
        <Button role="button" selected={mode === 'rgb'} onClick={() => setMode('rgb')}>RGB</Button>
      </div>
      <PlayerColorPicker key={data.id} value={data.value} mode={mode}
        onChange={value => act('color-picker-change', { id: data.id, value })} />
    </main>
  </div>;
};
