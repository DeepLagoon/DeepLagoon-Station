import { useEffect, useRef, useState } from 'react';

// Coordinates are in the original artwork. This overlay uses the same middle
// nine-slice as the shell, so the caps stay registered when the window resizes.
const shipKeys = [
  ['Escape', 350, 929, 70, 53], ['Tab', 438, 929, 69, 53],
  ['KeyM', 527, 929, 71, 53], ['KeyC', 620, 929, 73, 53],
  ['KeyS', 720, 929, 66, 53], ['ArrowLeft', 807, 929, 62, 53],
  ['ArrowRight', 889, 929, 62, 53], ['Minus', 973, 929, 53, 53],
  ['Equal', 1045, 929, 53, 53], ['Enter', 1118, 929, 127, 53],
];
const civilianKeys = [
  ...['Escape', 'Digit1', 'Digit2', 'Digit3', 'Digit4', 'Digit5', 'Digit6', 'Digit7', 'Digit8', 'Digit9', 'Digit0', 'Minus', 'Equal', 'Backspace'].map((key, i) =>
    [key, i ? 212 + (i - 1) * 58 : 119, 844, i === 0 ? 87 : i === 13 ? 104 : 51, 18]),
  ['Tab', 113, 868, 109, 22],
  ...'QWERTYUIOP'.split('').map((key, i) => [`Key${key}`, 230 + i * 60, 868, 51, 22]),
  ['BracketLeft', 831, 868, 52, 22], ['BracketRight', 891, 868, 52, 22], ['Backslash', 1009, 868, 125, 22],
  ['CapsLock', 103, 897, 136, 23],
  ...'ASDFGHJKL'.split('').map((key, i) => [`Key${key}`, 248 + i * 62, 897, 54, 23]),
  ['Semicolon', 808, 897, 54, 23], ['Quote', 869, 897, 52, 23], ['Enter', 984, 897, 157, 23],
  ['ShiftLeft', 93, 927, 92, 30], ['ControlLeft', 194, 927, 61, 30], ['AltLeft', 264, 927, 61, 30],
  ['Space', 403, 927, 411, 30], ['AltRight', 825, 927, 60, 30],
  ['ArrowLeft', 1176, 929, 54, 28], ['ArrowDown', 1240, 929, 54, 28], ['ArrowRight', 1305, 929, 139, 28],
];

export const ConsoleKeyboard = ({ kind, frame }) => {
  const [pressed, setPressed] = useState([]);
  const sources = useRef(new Map());
  const ship = kind === 'ship';
  const keys = ship ? shipKeys : civilianKeys;
  const image = ship ? 'ship-console-shell.png' : 'civilian-console-shell.webp';
  useEffect(() => {
    let timer;
    const update = () => setPressed([...sources.current.values()]);
    const clear = () => { clearTimeout(timer); sources.current.clear(); update(); };
    const down = event => {
      if (event.repeat) return;
      sources.current.set(event.code, event.code); update();
    };
    const up = event => { sources.current.delete(event.code); update(); };
    const pointer = event => {
      if (!frame.current?.contains(event.target) || event.button !== 0) return;
      sources.current.set('pointer', 'Enter'); update();
    };
    const release = () => {
      // Leave a short visible travel even for a quick tap.
      clearTimeout(timer);
      timer = setTimeout(() => { sources.current.delete('pointer'); update(); }, 100);
    };
    const visibility = () => { if (document.hidden) clear(); };
    const nativeKey = event => {
      const { code, down } = event.detail || {};
      if (typeof code !== 'string') return;
      if (down) sources.current.set(`native:${code}`, code === 'Pointer' ? 'Enter' : code);
      else sources.current.delete(`native:${code}`);
      update();
    };
    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('pointerdown', pointer, true);
    window.addEventListener('pointerup', release, true);
    window.addEventListener('pointercancel', clear);
    window.addEventListener('blur', clear);
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('deeplagoon/console-key', nativeKey);
    return () => {
      clearTimeout(timer); sources.current.clear();
      window.removeEventListener('keydown', down); window.removeEventListener('keyup', up);
      window.removeEventListener('pointerdown', pointer, true); window.removeEventListener('pointerup', release, true);
      window.removeEventListener('pointercancel', clear); window.removeEventListener('blur', clear);
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('deeplagoon/console-key', nativeKey);
    };
  }, [frame]);
  // Letter input without a dedicated ship key lights COMM; civilian lower-row
  // letter input uses its long space cap. No extra game action is emitted.
  const mapped = pressed.map(code => keys.some(key => key[0] === code) ? code : ship ? 'KeyC' : 'Space');
  return <svg className={`ConsoleKeyboard ConsoleKeyboard--${kind}`} aria-hidden="true" focusable="false"
    viewBox={ship ? '114 898 1308 126' : '112 808 1312 216'} preserveAspectRatio="none">
    {keys.filter(([code]) => mapped.includes(code)).map(([code, x, y, width, height]) =>
      <svg key={code} data-key={code} data-pressed="true" x={x} y={y} width={width} height={height}
        viewBox={`${x} ${y} ${width} ${height}`} overflow="hidden">
        <rect x={x} y={y} width={width} height={height} rx="3" fill="#080b0d" />
        <image href={image} width="1536" height="1024" transform="translate(0 3)" />
        <rect x={x} y={y} width={width} height={height} rx="3" fill="#000" fillOpacity=".24" />
        <path d={`M${x},${y + 2}h${width}`} stroke="#000" strokeWidth="4" />
      </svg>)}
  </svg>;
};
