import { useId } from 'react';
import { useBackend } from '../backend';
import { BackpackWindows } from './BackpackEditor';

export function BackpackReview() {
  const { act, data } = useBackend();
  const dialogId = useId();
  return <BackpackWindows key={data.backpack?.container || ''} data={data.backpack} act={act} dialogId={dialogId} review />;
}
