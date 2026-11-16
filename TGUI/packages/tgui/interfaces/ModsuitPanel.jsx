import { useState } from 'react';

import { useBackend } from '../backend';
import {
  Box, Button, Collapsible, Input, LabeledList, ProgressBar, Section, Stack, Table,
} from '../components';
import { Layout, Window } from '../layouts';

const partSlots = ['head', 'outerClothing', 'gloves', 'shoes'];
const statColumns = [
  ['save', 'complexity'],
  ['plug', 'idleDraw'],
  ['lightbulb', 'activeDraw'],
  ['bolt', 'useDraw'],
  ['hourglass-half', 'cooldown'],
  ['tasks', 'actions'],
];

function EditableValue({ initialValue, label, placeholder, onApply }) {
  const [draft, setDraft] = useState(initialValue);

  return (
    <>
      <Input value={draft} placeholder={placeholder} onInput={(_, value) => setDraft(value)}
        onEnter={(_, value) => onApply(value)} />
      <Button icon="check" tooltip={label} onClick={() => onApply(draft)} />
    </>
  );
}

function LinkControls({ data, act, view }) {
  const { labels, wearable, linkName, linkNumber, frequency, canSetFrequency, call, callStatus, contacts } = data;
  if (!view) return null;

  return (
    <Box mb={1}>
      {view === 'settings' && (
        <LabeledList>
          {wearable && (
            <LabeledList.Item label="MODlink">
              <EditableValue
                key={linkName}
                initialValue={linkName === linkNumber ? '' : linkName}
                label={labels.rename}
                placeholder={linkNumber}
                onApply={value => act('Rename', { value })}
              />
            </LabeledList.Item>
          )}
          <LabeledList.Item label={labels.frequency}>
            {canSetFrequency ? (
              <EditableValue key={frequency} initialValue={String(frequency)} label={labels.tune}
                onApply={value => act('SetFrequency', { value })} />
            ) : (frequency || labels.none)}
          </LabeledList.Item>
        </LabeledList>
      )}
      {view === 'contacts' && (
        <>
          <Box mb={0.5}>{callStatus}</Box>
          {call === 'Idle' && contacts.map(contact => (
            <Button key={contact.id} icon="phone" onClick={() => act('Call', { target: contact.id })}>
              {contact.name}
            </Button>
          ))}
          {call === 'Incoming' && (
            <Button icon="phone" onClick={() => act('Accept')}>{labels.accept}</Button>
          )}
          {call !== 'Idle' && (
            <Button icon="phone-slash" onClick={() => act('End')}>
              {call === 'Incoming' ? labels.decline : labels.end}
            </Button>
          )}
        </>
      )}
    </Box>
  );
}

function Parameters({ data, act }) {
  const { labels, control, active, busy, service, wearable, selected, wearer, complexity, capacity } = data;
  const [linkView, setLinkView] = useState('');
  const toggleLinkView = view => setLinkView(previous => previous === view ? '' : view);
  const hasCall = data.call !== 'Idle';

  return (
    <Section title={control ? labels.parameters : 'MODlink'}>
      <LabeledList>
        {control && (
          <LabeledList.Item
            label={labels.status}
            buttons={wearable && (
              <Button icon="power-off" disabled={busy || service} onClick={() => act('ToggleSuit')}>
                {active ? labels.deactivate : labels.activate}
              </Button>
            )}>
            {busy ? labels.busy : active ? labels.active : labels.idle}
          </LabeledList.Item>
        )}
        <LabeledList.Item
          label="MODlink"
          buttons={(
            <>
              <Button icon="pen" tooltip={labels.rename} selected={linkView === 'settings'}
                disabled={!wearable} onClick={() => toggleLinkView('settings')} />
              <Button icon={hasCall ? 'phone-slash' : 'phone'} selected={linkView === 'contacts'}
                disabled={!wearable} onClick={() => hasCall && data.call !== 'Incoming'
                  ? act('End') : toggleLinkView('contacts')}>
                {hasCall && data.call !== 'Incoming' ? labels.end : labels.call}
              </Button>
            </>
          )}>
          {data.linkName} / {data.frequency || labels.none}
        </LabeledList.Item>
        {(linkView || hasCall) && (
          <LabeledList.Item>
            <LinkControls data={data} act={act} view={linkView || 'contacts'} />
          </LabeledList.Item>
        )}
        {control && (
          <>
            <LabeledList.Item label={labels.cover}>{service ? labels.open : labels.closed}</LabeledList.Item>
            <LabeledList.Item label={labels.selected}>{selected || labels.none}</LabeledList.Item>
            <LabeledList.Item label={labels.complexity}>{complexity} ({capacity})</LabeledList.Item>
            <LabeledList.Item label={labels.wearer}>{wearer || labels.none}</LabeledList.Item>
            {wearable && (
              <LabeledList.Item label="Hardlight">
                <Button icon="palette" onClick={() => act('OpenHardlightStyle')}>{labels.hardlight}</Button>
              </LabeledList.Item>
            )}
          </>
        )}
      </LabeledList>
    </Section>
  );
}

function Hardware({ data }) {
  const { labels, name, parts, cell, charge } = data;
  return (
    <Section title={labels.hardware}>
      <Collapsible title={labels.parts}>
        <LabeledList>
          <LabeledList.Item label={labels.control}>{name}</LabeledList.Item>
          {partSlots.map(slot => {
            const part = parts.find(entry => entry.slot === slot);
            return (
              <LabeledList.Item key={slot} label={labels[slot]}>
                {part ? part.name : labels.none}
                {part && <Box inline ml={0.5} color="label">({part.deployed ? labels.deployed : labels.stowed})</Box>}
              </LabeledList.Item>
            );
          })}
        </LabeledList>
      </Collapsible>
      <Collapsible title={labels.cell}>
        {charge < 0 ? <Box color="bad" textAlign="center">{labels.empty}</Box> : (
          <LabeledList>
            <LabeledList.Item label={labels.cellType}>{cell}</LabeledList.Item>
            <LabeledList.Item label={labels.cellCharge}>
              <ProgressBar value={charge / 100} ranges={{ good: [0.6, 1], average: [0.3, 0.6], bad: [0, 0.3] }}>
                {charge}%
              </ProgressBar>
            </LabeledList.Item>
          </LabeledList>
        )}
      </Collapsible>
    </Section>
  );
}

function Module({ module, data, act }) {
  const { id, active, name, usable, requiresPower, pinned, configurable, temperature } = module;
  const { labels, capacity, wearable, service, busy } = data;
  const [configure, setConfigure] = useState(false);
  const send = (operation, value) => act(operation, { target: id, value });

  return (
    <Collapsible title={name}>
      <Section>
        <Table>
          <Table.Row header>
            {statColumns.map(([icon, key]) => (
              <Table.Cell key={key} textAlign="center">
                <Button icon={icon} tooltip={labels[key]} color="transparent" />
              </Table.Cell>
            ))}
          </Table.Row>
          <Table.Row>
            <Table.Cell textAlign="center">{module.complexity}/{capacity}</Table.Cell>
            <Table.Cell textAlign="center">0</Table.Cell>
            <Table.Cell textAlign="center">0</Table.Cell>
            <Table.Cell textAlign="center">{module.useCharge}</Table.Cell>
            <Table.Cell textAlign="center">{module.cooldown}</Table.Cell>
            <Table.Cell textAlign="center">
              <Button icon="bullseye" tooltip={active ? labels.stop : labels.use} selected={active}
                disabled={!usable || !wearable || (requiresPower && (!data.active || busy))}
                onClick={() => send('ToggleModule')} />
              <Button icon="thumbtack" tooltip={pinned ? labels.pinned : labels.pin} selected={pinned}
                disabled={!usable || pinned || !wearable} onClick={() => send('PinModule')} />
              <Button icon="cog" tooltip={labels.configure} selected={configure} disabled={!configurable || !wearable}
                onClick={() => setConfigure(!configure)} />
              {service && <Button icon="eject" tooltip={labels.remove} onClick={() => send('RemoveModule')} />}
            </Table.Cell>
          </Table.Row>
        </Table>
        {configure && configurable && wearable && (
          <LabeledList>
            <LabeledList.Item label={labels.temperature}>
              <EditableValue key={temperature} initialValue={String(temperature)} label={labels.applyTemperature}
                onApply={value => send('ConfigureModule', value)} />
            </LabeledList.Item>
          </LabeledList>
        )}
        <Box>{module.description}</Box>
      </Section>
    </Collapsible>
  );
}

export const ModsuitPanel = () => {
  const { data, act } = useBackend();
  const { labels, control, info, modules } = data;
  return (
    <Layout className="Window">
      <Window.Content scrollable>
        {data.ready && labels && (
          <Stack vertical fill>
            <Stack.Item><Parameters data={data} act={act} /></Stack.Item>
            {control && (
              <>
                <Stack.Item><Hardware data={data} /></Stack.Item>
                <Stack.Item>
                  <Section title={labels.info}><Box textAlign="center">{info || labels.noInfo}</Box></Section>
                </Stack.Item>
                <Stack.Item grow>
                  <Section title={labels.modules} fill>
                    {modules.length === 0 && <Box textAlign="center">{labels.noModules}</Box>}
                    {modules.map(module => <Module key={module.id} module={module} data={data} act={act} />)}
                  </Section>
                </Stack.Item>
              </>
            )}
          </Stack>
        )}
      </Window.Content>
    </Layout>
  );
};
