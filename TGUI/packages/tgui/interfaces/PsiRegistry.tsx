import { useState } from 'react';
import { useBackend } from '../backend';
import {
  Box,
  Button,
  Divider,
  LabeledList,
  Section,
  Stack,
} from '../components';
import { Layout, Window } from '../layouts';

type PsiRegistryRecord = {
  key: number;
  name: string;
  job: string;
  status: string;
  reason: string;
  selected?: boolean;
};

type Data = {
  labels: Record<string, string>;
  canEdit?: boolean;
  hasSelection?: boolean;
  selectedKey?: number;
  selectedName?: string;
  selectedJob?: string;
  selectedStatus?: string;
  selectedReason?: string;
  records?: PsiRegistryRecord[];
};

const maxReasonLength = 256;

const ReasonInput = (props: {
  value: string;
  placeholder: string;
  disabled: boolean;
  onChange: (value: string) => void;
}) => {
  const { value, placeholder, disabled, onChange } = props;
  return (
    <Box
      className="Input Input--fluid"
      style={disabled ? { opacity: 0.5 } : undefined}>
      <Box className="Input__baseline">.</Box>
      <input
        className="Input__input"
        type="text"
        value={value}
        maxLength={maxReasonLength}
        placeholder={placeholder}
        disabled={disabled}
        aria-label={placeholder}
        onChange={event => onChange(event.currentTarget.value)}
      />
    </Box>
  );
};

export const PsiRegistry = () => {
  const { act, data } = useBackend<Data>();
  const [reason, setReason] = useState('');
  const labels = data.labels || {};
  const t = (key: string) => labels[key] || key;
  const canEdit = !!data.canEdit;
  const hasSelection = !!data.hasSelection;
  const records = Array.isArray(data.records) ? data.records : [];

  const changeStatus = (value: string, withReason: boolean) => {
    act('status', withReason ? { value, reason } : { value });
  };

  const hasReason = reason.trim().length > 0;
  const canSetReasoned = canEdit && hasReason;

  return (
    <Layout className="Window" aria-label={t('title')}>
      <Window.Content scrollable>
        <Section title={t('title')}>
          <Box color={canEdit ? 'good' : 'bad'} role="status">
            {canEdit ? t('accessGranted') : t('accessDenied')}
          </Box>
        </Section>
        <Stack>
          <Stack.Item basis="13rem" grow={0}>
            <Section title={t('list')}>
              {!records.length && (
                <Box color="label" italic textAlign="center" py={1}>
                  {t('empty')}
                </Box>
              )}
              <Stack vertical>
                {records.map(record => (
                  <Stack.Item key={record.key}>
                    <Button
                      fluid
                      selected={!!record.selected}
                      aria-pressed={!!record.selected}
                      onClick={() => act('select', { key: record.key })}>
                      <Box bold>{record.name}</Box>
                      <Box
                        fontSize="0.85em"
                        color={record.selected ? undefined : 'label'}>
                        {record.status}
                      </Box>
                    </Button>
                  </Stack.Item>
                ))}
              </Stack>
            </Section>
          </Stack.Item>
          <Stack.Item grow>
            <Section>
              {!hasSelection ? (
                <Box color="label" italic textAlign="center" py={2}>
                  {t('selectRecord')}
                </Box>
              ) : (
                <>
                  <Box bold fontSize="1.15em">
                    {data.selectedName}
                  </Box>
                  <Divider />
                  <LabeledList>
                    <LabeledList.Item label={t('job')}>
                      {data.selectedJob}
                    </LabeledList.Item>
                    <LabeledList.Item label={t('status')}>
                      {data.selectedStatus}
                    </LabeledList.Item>
                    <LabeledList.Item label={t('reason')}>
                      {data.selectedReason}
                    </LabeledList.Item>
                  </LabeledList>
                  <Divider />
                  <Box bold mt={1} mb={0.5}>
                    {t('reasonLabel')}
                  </Box>
                  <ReasonInput
                    value={reason}
                    placeholder={t('reasonPlaceholder')}
                    disabled={!canEdit}
                    onChange={setReason}
                  />
                  <Box bold mt={1} mb={0.5}>
                    {t('setStatus')}
                  </Box>
                  <Stack>
                    <Stack.Item>
                      <Button
                        content={t('clean')}
                        disabled={!canSetReasoned}
                        tooltip={canEdit && !hasReason ? t('reasonRequired') : undefined}
                        onClick={() => changeStatus('Registered', true)}
                      />
                    </Stack.Item>
                    <Stack.Item>
                      <Button
                        content={t('suspected')}
                        disabled={!canSetReasoned}
                        tooltip={canEdit && !hasReason ? t('reasonRequired') : undefined}
                        onClick={() => changeStatus('Suspected', true)}
                      />
                    </Stack.Item>
                    <Stack.Item>
                      <Button
                        content={t('abusing')}
                        disabled={!canSetReasoned}
                        tooltip={canEdit && !hasReason ? t('reasonRequired') : undefined}
                        onClick={() => changeStatus('Abusing', true)}
                      />
                    </Stack.Item>
                    <Stack.Item>
                      <Button
                        content={t('reset')}
                        disabled={!canEdit}
                        onClick={() => changeStatus('None', false)}
                      />
                    </Stack.Item>
                  </Stack>
                </>
              )}
            </Section>
          </Stack.Item>
        </Stack>
      </Window.Content>
    </Layout>
  );
};
