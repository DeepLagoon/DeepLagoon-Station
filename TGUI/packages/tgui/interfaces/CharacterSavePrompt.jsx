import { useBackend } from '../backend';
import { Button } from '../components';
import { playerTheme } from '../components/PlayerTheme';
import { editorAppearance } from './CharacterEditorSettings';
import './CharacterEditor.scss';
export const CharacterSavePrompt = () => {
  const { data, act } = useBackend();
  const appearance = data.appearance || {};
  const theme = playerTheme(appearance);
  return <div className={'CharacterEditor CharacterEditor--save Chat ' + theme.className} style={{ ...theme.style, ...editorAppearance(appearance) }}>
    <header className="CharacterEditor__windowTitle">Несохранённые изменения</header>
    <main><p>Сохранить изменения персонажа перед выходом?</p>
      <div className="CharacterEditor__saveActions"><Button color="good" onClick={() => act('save')}>Сохранить</Button><Button onClick={() => act('discard')}>Не сохранять</Button><Button onClick={() => act('cancel')}>Отмена</Button></div>
    </main>
  </div>;
};
