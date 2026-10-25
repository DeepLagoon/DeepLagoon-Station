export function tintSprite(image, color = '#ffffffff') {
  const canvas = document.createElement('canvas');
  canvas.width = image.width; canvas.height = image.height;
  const context = canvas.getContext('2d');
  context.drawImage(image, 0, 0);
  const pixels = context.getImageData(0, 0, canvas.width, canvas.height);
  const hex = color.replace('#', '');
  const rgba = [0, 2, 4, 6].map((offset, channel) => channel === 3 && hex.length < 8 ? 255 : parseInt(hex.slice(offset, offset + 2), 16));
  for (let i = 0; i < pixels.data.length; i += 4)
    for (let channel = 0; channel < 4; channel++) pixels.data[i + channel] *= rgba[channel] / 255;
  context.putImageData(pixels, 0, 0);
  return canvas;
}
