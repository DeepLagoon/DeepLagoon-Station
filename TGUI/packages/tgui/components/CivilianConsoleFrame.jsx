import { useRef } from 'react';
import { ConsoleKeyboard } from './ConsoleKeyboard';
import './CivilianConsoleFrame.scss';

// Approved civilian hardware, kept separate from every live screen and label.
// A local nine-slice preserves the bezel and keyboard when the window resizes.
export const CivilianConsoleFrame = ({ children, className = '', ...props }) => {
  const frame = useRef(null);
  return <div {...props} ref={frame} className={`CivilianConsoleFrame ${className}`}
    style={{ borderImageSource: 'url("civilian-console-shell.webp")' }}>
    {children}
    <ConsoleKeyboard kind="civilian" frame={frame} />
  </div>;
};

export const CivilianConsoleButton = ({ className = '', ...props }) => (
  <button {...props} className={`CivilianConsoleButton ${className}`} />
);
