import { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { DiskConsoleScreen } from '../packages/tgui/interfaces/DiskConsole';

const data = { title:'Терминал дисков исследований', totalLabel:'На выбранном сервере имеется 17000 очков',
  serverLabel:'Список серверов', closeLabel:'Закрыть', waitingLabel:'Ожидание данных сервера…', ready:true,
  entries:[{id:'1k',label:'Печать диска очков (1000)'},{id:'5k',label:'Печать диска очков (5000)'},{id:'10k',label:'Печать диска очков (10000)'}] };

function Variant({ layout }) {
  const stage = useRef(null);
  const [notice, setNotice] = useState('Демонстрационные данные');
  const [closed, setClosed] = useState(false);
  useEffect(() => {
    const element = stage.current?.querySelector('.CivilianConsoleFrame');
    if (!element || !globalThis.Tweak) return;
    const state = { accent: layout === 'instrument' ? '#9bcbd0' : layout === 'ledger' ? '#c7bd9c' : '#a4bccc' };
    const tweak = new Tweak({container:element,onChange:()=>element.style.setProperty('--dc-accent',state.accent)});
    tweak.addColorPicker(state,'accent',{label:'Акцент экрана',reference:'--dc-accent'});
    return () => tweak.dispose();
  }, [closed]);
  const act = (action, payload) => {
    if (action === 'close') { setClosed(true); return; }
    setNotice(action === 'server' ? 'Демонстрация: выбор сервера' : `Демонстрация: выбран диск ${payload.id}`);
  };
  return <div ref={stage} style={{height:'100%'}}>
    {closed ? <button className="CivilianPreviewRestore" onClick={()=>setClosed(false)}>Открыть терминал</button> : <>
      <DiskConsoleScreen data={data} act={act} layout={layout} />
      <output className="CivilianPreviewNotice" aria-live="polite">{notice}</output>
    </>}
  </div>;
}

for (const layout of ['compact','instrument','ledger'])
  createRoot(document.getElementById(`civilian-${layout}`)).render(<Variant layout={layout} />);
