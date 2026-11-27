import { useBackend } from '../backend';
import {
  Box,
  Button,
  LabeledList,
  ProgressBar,
  Section,
  Stack,
  Table,
} from '../components';
import { Layout, Window } from '../layouts';

type Signature = {
  type: string;
  sector: string;
  name: string;
  intensity: number;
};

type Data = {
  labels: Record<string, string>;
  glimmer: number;
  range: number;
  hunter: boolean;
  map: boolean;
  ready: boolean;
  signatures: Signature[];
};

const intensityRanges = {
  bad: [0, 30],
  average: [31, 70],
  good: [71, 100],
};

const percentOf = (value: number) => {
  const percent = Math.round(Number(value));
  return Number.isFinite(percent) ? Math.min(100, Math.max(0, percent)) : 0;
};

export const PsiSpectrometer = () => {
  const { data, act } = useBackend<Data>();
  const labels = data.labels || {};
  const t = (key: string) => labels[key] || key;
  const ready = !!data.ready;
  const hunter = !!data.hunter;
  const mapOverlay = !!data.map;
  const signatures = data.signatures || [];

  return (
    <Layout className="Window" aria-label={t('title')}>
      <Window.Content scrollable>
        <Section
          title={t('title')}
          buttons={(
            <>
              <Button
                content={t('hunter')}
                icon={hunter ? 'toggle-on' : 'toggle-off'}
                selected={hunter}
                aria-pressed={hunter}
                onClick={() => act('hunter', { on: !hunter })}
              />
              <Button
                content={t('map')}
                icon={mapOverlay ? 'map-location-dot' : 'map'}
                selected={mapOverlay}
                aria-pressed={mapOverlay}
                onClick={() => act('map', { on: !mapOverlay })}
              />
            </>
          )}>
          <LabeledList>
            <LabeledList.Item label={t('glimmer')}>
              {data.glimmer}
            </LabeledList.Item>
            <LabeledList.Item label={t('range')}>
              {data.range}
            </LabeledList.Item>
          </LabeledList>
          <Stack align="center" mt={1}>
            <Stack.Item>
              <Button
                content={t('scan')}
                icon="satellite-dish"
                disabled={!ready}
                onClick={() => act('scan')}
              />
            </Stack.Item>
            <Stack.Item>
              <Box color={ready ? 'good' : 'average'} role="status">
                {ready ? t('ready') : t('recharging')}
              </Box>
            </Stack.Item>
          </Stack>
        </Section>
        <Section title={t('signature')}>
          <Table>
            <Table.Row header>
              <Table.Cell>{t('signature')}</Table.Cell>
              <Table.Cell>{t('sector')}</Table.Cell>
              <Table.Cell>{t('subject')}</Table.Cell>
              <Table.Cell collapsing textAlign="right">
                {t('intensity')}
              </Table.Cell>
            </Table.Row>
            {signatures.map((signature, index) => {
              const percent = percentOf(signature.intensity);
              return (
                <Table.Row key={index}>
                  <Table.Cell>{signature.type}</Table.Cell>
                  <Table.Cell>{signature.sector}</Table.Cell>
                  <Table.Cell>{signature.name}</Table.Cell>
                  <Table.Cell collapsing>
                    <Box width="9em">
                      <ProgressBar
                        value={percent}
                        minValue={0}
                        maxValue={100}
                        ranges={intensityRanges}>
                        {percent}%
                      </ProgressBar>
                    </Box>
                  </Table.Cell>
                </Table.Row>
              );
            })}
          </Table>
          {!signatures.length && (
            <Box color="label" italic textAlign="center" py={1}>
              {t('empty')}
            </Box>
          )}
        </Section>
      </Window.Content>
    </Layout>
  );
};
