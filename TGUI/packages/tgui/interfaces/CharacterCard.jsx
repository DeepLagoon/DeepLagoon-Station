import { useState } from 'react';
import { useBackend } from '../backend';
import { Button, Collapsible, LabeledList, Section } from '../components';
import { CharacterText } from '../components/CharacterText';
import './CharacterCard.scss';

const statuses = ['Запрещено', 'Спросить', 'Разрешено'];
const colors = ['bad', 'average', 'good'];

export const CharacterCard = () => {
  const { data, act } = useBackend();
  const [imageIndex, setImageIndex] = useState(0);
  const [background, setBackground] = useState(0);
  const images = (data.headshots || []).filter(image => image.image);
  const selected = images.length ? imageIndex % images.length : 0;
  const headshot = images[selected]?.image || data.headshot;

  return (
    <div className={'CharacterCard' + (headshot ? ' CharacterCard--headshot' : '')}>
      <aside className="CharacterCard__sidebar">
        {headshot && (
          <section className="CharacterCard__art">
            <h3 className="CharacterCard__panelTitle">Арт персонажа</h3>
            <img className="CharacterCard__headshot" src={headshot} alt={'Арт ' + data.name} />
            <div className="CharacterCard__imageNavigation">
              {images.length > 1 && <>
                <Button icon="arrow-left" aria-label="Предыдущий арт"
                  onClick={() => setImageIndex((selected + images.length - 1) % images.length)} />
                <span>{selected + 1} / {images.length}</span>
                <Button icon="arrow-right" aria-label="Следующий арт"
                  onClick={() => setImageIndex((selected + 1) % images.length)} />
              </>}
            </div>
          </section>
        )}
        <section className="CharacterCard__modelPanel">
          <h3 className="CharacterCard__panelTitle">Модель персонажа</h3>
          <div className={'CharacterCard__model CharacterCard__model--background' + background} />
          <div className="CharacterCard__modelControls">
            <Button icon="undo" aria-label="Повернуть влево" onClick={() => act('char_left')} />
            <Button onClick={() => setBackground((background + 1) % 3)}>Сменить фон</Button>
            <Button icon="redo" aria-label="Повернуть вправо" onClick={() => act('char_right')} />
          </div>
        </section>
      </aside>
      <main className="CharacterCard__details">
        <header className="CharacterCard__header">
          <div><h2>{data.name}</h2>{data.speciesName && <small>{data.speciesName}</small>}</div>
          <Button color="bad" icon="times" onClick={() => act('close')}>Закрыть</Button>
        </header>
        <Collapsible title="Описание персонажа" open>
          <Section><div className="CharacterText"><CharacterText text={data.flavor || '———'} /></div></Section>
        </Collapsible>
        <Collapsible title="Внеигровые заметки" open>
          <Section><div className="CharacterText"><CharacterText text={data.ooc || '———'} /></div></Section>
        </Collapsible>
        <Section title="Предпочтения персонажа">
          <LabeledList>
            {['erp', 'noncon', 'vore'].map((key, index) => (
              <LabeledList.Item key={key}
                label={['Эротические взаимодействия', 'Взаимодействия без согласия', 'Vore'][index]}
                color={colors[data[key]]}>
                {statuses[data[key]] || '———'}
              </LabeledList.Item>
            ))}
          </LabeledList>
        </Section>
      </main>
    </div>
  );
};
