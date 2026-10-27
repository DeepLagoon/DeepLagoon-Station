import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { Button, Dropdown } from '../components';
import { chatComposerChannelStyle } from './chatComposerColors';
import './ChatComposer.scss';

export const ChatComposer = () => {
  const { data, act: sendAction } = useBackend();
  const act = (action, payload = {}) => sendAction(action, { ...payload, session: data.session });
  const [text, setText] = useState(data.text || '');
  const input = useRef();
  const timer = useRef();
  const latest = useRef(text);
  const composing = useRef(false);
  const channel = String(data.channel || '');
  const emote = channel === String(data.emoteChannel);
  useEffect(() => {
    let second;
    const first = requestAnimationFrame(() => {
      second = requestAnimationFrame(() => act('presented'));
    });
    return () => {
      cancelAnimationFrame(first);
      cancelAnimationFrame(second);
    };
  }, [data.revision]);
  useEffect(() => {
    const cycle = event => {
      if (event.key === 'Tab' && !event.shiftKey && !event.altKey && !event.metaKey
          && !composing.current && !event.isComposing) {
        event.preventDefault();
        if (!event.repeat) {
          clearTimeout(timer.current);
          const channels = data.channels || [];
          const index = channels.findIndex(option => String(option.id) === channel);
          if (channels.length) {
            const next = (index + (event.ctrlKey ? -1 : 1) + channels.length) % channels.length;
            act('channel', { text: latest.current, channel: String(channels[next].id) });
          }
        }
      }
    };
    document.addEventListener('keydown', cycle, true);
    return () => {
      document.removeEventListener('keydown', cycle, true);
    };
  }, [data.revision, data.channels, channel]);
  useEffect(() => {
    clearTimeout(timer.current);
    setText(data.text || '');
    latest.current = data.text || '';
  }, [data.revision]);
  useEffect(() => {
    const focus = () => input.current?.focus({ preventScroll: true });
    window.addEventListener('deeplagoon/focus-input', focus);
    focus();
    return () => {
      window.removeEventListener('deeplagoon/focus-input', focus);
      clearTimeout(timer.current);
    };
  }, []);
  useEffect(() => {
    input.current?.focus();
    input.current?.setSelectionRange(input.current.value.length, input.current.value.length);
    act('focus');
  }, [channel, data.revision]);
  const dispatch = (action, value = latest.current, nextChannel = channel) => {
    clearTimeout(timer.current);
    act(action, { text: value, channel: nextChannel });
  };
  const change = value => {
    setText(value);
    latest.current = value;
    clearTimeout(timer.current);
    timer.current = setTimeout(() => dispatch('draft', latest.current), 180);
  };
  return <div className="ChatComposer__stage">
    <section className={'ChatComposer' + (emote ? ' ChatComposer--emote' : '')} aria-label="Ввод сообщения">
      <header>
        <div><strong>{emote ? 'Описать действие' : 'Написать сообщение'}</strong></div>
      </header>
      <div className="ChatComposer__channel"><span>Канал</span><Dropdown
        selected={channel} width="180px"
        style={chatComposerChannelStyle(channel, data.chatAppearance)}
        options={(data.channels || []).map(option => ({ value: String(option.id), displayText: option.name }))}
        onSelected={value => dispatch('channel', latest.current, value)} /></div>
      <textarea ref={input} value={text} maxLength={data.maxLength} aria-label="Сообщение"
        placeholder={emote ? 'Опишите действие персонажа…' : 'Введите сообщение…'}
        onChange={event => change(event.target.value)}
        onCompositionStart={() => { composing.current = true; act('composition', { active: true }); }}
        onCompositionEnd={() => { composing.current = false; act('composition', { active: false }); }}
        onKeyDown={event => {
          if (event.key === 'Escape') { event.preventDefault(); dispatch('cancel'); }
          if (event.key === 'Backspace' && event.currentTarget.value === ''
              && !composing.current && !event.nativeEvent.isComposing) {
            event.preventDefault();
            dispatch('cancel');
          }
          if ((event.key === 'Enter' || event.code === 'NumpadEnter' || event.keyCode === 13)
              && !composing.current && !event.nativeEvent.isComposing) {
            event.preventDefault();
            if (event.repeat) return;
            if (event.shiftKey) {
              const field = event.currentTarget;
              const start = field.selectionStart;
              const value = latest.current.slice(0, start) + '\n' + latest.current.slice(field.selectionEnd);
              if (value.length <= data.maxLength) {
                change(value);
                requestAnimationFrame(() => field.setSelectionRange(start + 1, start + 1));
              }
            } else if (latest.current.trim()) dispatch('submit');
          }
        }} />
      <footer><small>{text.length}/{data.maxLength}</small>
        <Button icon="paper-plane" disabled={!text.trim()} onClick={() => dispatch('submit')}>Отправить</Button></footer>
    </section>
  </div>;
};
