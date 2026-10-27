// Channel IDs match ChatSelectChannel; defaults match the chat's goon themes.
const channels = {
  1: ['say'],
  2: ['whisper', '#97a3bd', '#5a6478'],
  16: ['radio', '#1ecc43', '#008000'],
  32: ['looc', '#6699cc', '#6699cc'],
  64: ['ooc', '#cca300', '#002eb8'],
  512: ['emote', '#d6d6e0', '#4a4a55'],
  1024: ['deadsay', '#e2c1ff', '#5c00e6'],
  8192: ['adminsay', '#ff4500', '#ff4500'],
  16384: ['telepathy', '#6fd0e0', '#2a8a9c'],
  65536: ['telepathy', '#6fd0e0', '#2a8a9c'],
};

const plainColors = {
  terminal: '#33ff33',
  retro: '#ffb000',
  cyberpunk: '#00ffd5',
  sakura: '#f0d0d8',
  space: '#c8d0ff',
  vaporwave: '#e0d0ff',
};

export const chatComposerChannelStyle = (channel, appearance = {}) => {
  const [style, dark, light] = channels[channel] || channels[1];
  const override = appearance.styleOverrides?.[style];
  const isLight = appearance.theme === 'light';
  const plain = appearance.chatTextColor
    || (isLight ? '#000000' : plainColors[appearance.chatStyle] || '#abc6ec');
  const color = override?.disabled ? plain
    : override?.color || (isLight ? light : dark) || plain;
  const rgb = /^#[\da-f]{6}$/i.test(color) ? color : '#abc6ec';
  const components = [1, 3, 5].map(offset => {
    const value = parseInt(rgb.slice(offset, offset + 2), 16) / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  const luminance = components[0] * 0.2126 + components[1] * 0.7152 + components[2] * 0.0722;
  return { backgroundColor: color, color: luminance > 0.179 ? '#000000' : '#ffffff' };
};
