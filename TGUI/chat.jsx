// TGUI chat renderer, tabs, search, highlights and appearance settings.
// Game input and permission checks remain in SS14's ChatInputBox/ChatUIController.
import './packages/tgui-panel/styles/main.scss';
import './packages/tgui-panel/styles/themes/light.scss';
import './chat.scss';
import { combineReducers, useSelector } from 'common/redux';
import { useEffect, useRef, useState } from 'react';
import { playerTheme } from './packages/tgui/components/PlayerTheme';
import { Button, Section, Stack } from 'tgui/components';
import { Pane } from 'tgui/layouts';
import { createRenderer } from 'tgui/renderer';
import { configureStore } from 'tgui/store';
import { ChatPanel, ChatSearchBar, ChatTabs, chatMiddleware, chatReducer } from 'tgui-panel/chat';
import { chatRenderer } from 'tgui-panel/chat/renderer';
import { selectChat } from 'tgui-panel/chat/selectors';
import { settingsMiddleware, settingsReducer, SettingsPanel, useSettings } from 'tgui-panel/settings';
import { EmoteBar, emoteReducer } from './EmoteBar';

const store = configureStore({
  reducer: combineReducers({ chat: chatReducer, settings: settingsReducer, emotes: emoteReducer, lobby: (state={},action)=>action.type==='deeplagoon/lobby-status'?action.payload:state }),
  middleware: { pre: [chatMiddleware, settingsMiddleware] },
});
const Panel = () => {
  const settings = useSettings();
  const chat = useSelector(selectChat);
  const lobby = useSelector(state=>state.lobby);
  const resize = useRef(null);
  const [settingsHeight,setSettingsHeight] = useState(settings.settingsHeight || 260);
  useEffect(()=>{const height=Number(settings.settingsHeight);if(Number.isFinite(height)&&height>=100)setSettingsHeight(height);},[settings.settingsHeight]);
  const [searchOpen, setSearchOpen] = useState(false);
  return (
    <Pane theme={settings.theme === 'default' ? 'light' : settings.theme}>
      <Stack fill vertical>
        {lobby.server && <Stack.Item><div className={'LobbyStatus Chat '+playerTheme(settings).className} style={playerTheme(settings).style}>
          <strong>{lobby.server}</strong><span>{lobby.time}</span><span>{lobby.status}</span>
        </div></Stack.Item>}
        <Stack.Item><Section fitted><Stack align="center">
          <Stack.Item grow><ChatTabs /></Stack.Item>
          <Stack.Item><Button icon="pen" tooltip="Написать сообщение" onClick={() => Byond.topic({ type: 'act/compose', payload: {} })} /></Stack.Item>
          <Stack.Item><Button icon="search" onClick={() => setSearchOpen(!searchOpen)} /></Stack.Item>
          <Stack.Item><Button icon="cog" onClick={() => settings.toggle()} /></Stack.Item>
        </Stack></Section></Stack.Item>
        {searchOpen && <Stack.Item><ChatSearchBar /></Stack.Item>}
        {settings.visible && <Stack.Item shrink={0}><div className="ChatSettingsResize" style={{height:settingsHeight}}>
          <div className="ChatSettingsResize__contents"><SettingsPanel /></div>
          <div className="ChatSettingsResize__grip" role="separator" aria-label="Высота настроек" aria-orientation="horizontal" tabIndex={0}
            onPointerDown={e=>{resize.current={y:e.clientY,height:settingsHeight};e.currentTarget.setPointerCapture(e.pointerId);}}
            onPointerMove={e=>{if(resize.current)setSettingsHeight(Math.max(100,Math.min(window.innerHeight-110,resize.current.height+e.clientY-resize.current.y)));}}
            onPointerUp={()=>{resize.current=null;settings.update({settingsHeight});}} onPointerCancel={()=>{resize.current=null;}}
            onKeyDown={e=>{if(e.key==='ArrowUp'||e.key==='ArrowDown'){e.preventDefault();const height=Math.max(100,Math.min(window.innerHeight-110,settingsHeight+(e.key==='ArrowDown'?20:-20)));setSettingsHeight(height);settings.update({settingsHeight:height});}}} />
        </div></Stack.Item>}
        <Stack.Item><EmoteBar /></Stack.Item>
        <Stack.Item grow><Section fill fitted position="relative">
          <Pane.Content scrollable><ChatPanel lineHeight={settings.lineHeight} /></Pane.Content>
        </Section></Stack.Item>
        <Stack.Item><Button fluid onClick={() => chatRenderer.scrollToBottom?.()}>↓</Button></Stack.Item>
      </Stack>
    </Pane>
  );
};
const render = createRenderer(() => <Panel />);
const receive = message => {
  const parsed = Byond.parseJson(message);
  if (parsed.type === 'deeplagoon/chat-reset') {
    // A full reload clears both messages and renderer state, including erased messages.
    window.location.reload();
    return;
  }
  store.dispatch(parsed);
};
window.update = receive;
store.subscribe(render);
store.dispatch({ type: 'update', payload: { config: {
  interface: 'Chat', status: 2, window: { key: 'deeplagoon-chat', fancy: false },
}, data: {} } });
while (window.__updateQueue__.length) receive(window.__updateQueue__.shift());
render();
