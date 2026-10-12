import { useState } from 'react';
import { useBackend } from '../backend';
import { Button } from '../components';
import { playerTheme } from '../components/PlayerTheme';
import { editorAppearance } from './CharacterEditorSettings';
import './CharacterEditor.scss';
export const CharacterHeadshotLibrary = () => {
  const { data, act } = useBackend();
  const [remove, setRemove] = useState(null);
  const gallery = data.gallery || {}, images = gallery.images || [];
  const count = images.filter(image => image.active).length;
  const appearance = data.appearance || {};
  const theme = playerTheme(appearance);
  return <div className={'CharacterEditor CharacterEditor--library Chat ' + theme.className} style={{ ...theme.style, ...editorAppearance(appearance) }}>
    <header className="CharacterEditor__windowTitle">{gallery.library ? 'Моя библиотека' : 'Изображения профиля'}</header>
    <Button className="CharacterEditor__close" color="bad" onClick={() => act('headshot-library-close')}><span aria-hidden="true">×</span>Закрыть</Button>
    <main className="CharacterEditor__libraryContent">
      <h2>{data.name}</h2>
      <p>В профиле: {count} / {gallery.activeCapacity || 1}. В библиотеке: {images.length} / {gallery.capacity || 1}.</p>
      <div className="CharacterEditor__headshotLibrary">{images.map((image, index) => <article key={image.id}>
        {image.image ? <img src={image.image} alt={'Изображение ' + (index + 1)} /> : <p>Загрузка…</p>}
        <Button selected={image.primary} onClick={() => act('headshot-gallery', { operation: 'primary', value: image.id })}>{image.primary ? 'Основное' : 'Сделать основным'}</Button>
        {gallery.library && <Button.Checkbox checked={image.active} disabled={!image.active && count >= gallery.activeCapacity} onClick={() => act('headshot-gallery', { operation: 'toggle', value: image.id })}>Показывать в профиле</Button.Checkbox>}
        <Button onClick={() => act('headshot-upload', { value: image.id })}>Заменить</Button>
        <Button color="bad" onClick={() => { if (remove === image.id) { act('headshot-gallery', { operation: 'delete', value: image.id }); setRemove(null); } else setRemove(image.id); }}>{remove === image.id ? 'Подтвердить удаление' : 'Удалить'}</Button>
      </article>)}</div>
      {images.length < (gallery.capacity || 1) && <Button onClick={() => act('headshot-add')}>Добавить изображение</Button>}
      {remove && <Button onClick={() => setRemove(null)}>Отмена удаления</Button>}
      {gallery.error && <p role="alert">{gallery.error}</p>}{data.headshotStatus && <p>{data.headshotStatus}</p>}
    </main>
  </div>;
};
