import { useEffect, useRef, useState } from 'react';
import { useBackend } from '../backend';
import { Button, Dropdown } from '../components';
import './ChatComposer.scss';

export const ChatComposer = () => {
  const { data, act } = useBackend();
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
    const cycle = event => {
      if (event.key === 'Tab' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey
          && !composing.current && !event.isComposing) {
        event.preventDefault();
        if (!event.repeat) {
          clearTimeout(timer.current);
          act('cycle', { text: latest.current });
        }
      }
    };
    document.addEventListener('keydown', cycle, true);
    return () => {
      cancelAnimationFrame(first);
      cancelAnimationFrame(second);
      document.removeEventListener('keydown', cycle, true);
    };
  }, []);
  useEffect(() => {
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
        options={(data.channels || []).map(option => ({ value: String(option.id), displayText: option.name }))}
        onSelected={value => dispatch('channel', latest.current, value)} /></div>
      <textarea ref={input} value={text} maxLength={data.maxLength} aria-label="Сообщение"
        placeholder={emote ? 'Опишите действие персонажа…' : 'Введите сообщение…'}
        onChange={event => change(event.target.value)}
        onCompositionStart={() => { composing.current = true; act('composition', { active: true }); }}
        onCompositionEnd={() => { composing.current = false; act('composition', { active: false }); }}
        onKeyDown={event => {
          if (event.key === 'Escape') { event.preventDefault(); dispatch('cancel'); }
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
      <footer><span>Enter / Num Enter — отправить · Shift+Enter — новая строка · Tab — канал <small>{text.length}/{data.maxLength}</small></span>
        <Button icon="paper-plane" disabled={!text.trim()} onClick={() => dispatch('submit')}>Отправить</Button></footer>
    </section>
  </div>;
};
