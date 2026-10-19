import { useLayoutEffect } from 'react';
import { useBackend } from '../backend';

// A presentation change must not destroy a local draft or an active upload.
export const useInterfaceStyleGuard = (blocked) => {
  const { act } = useBackend();
  useLayoutEffect(() => {
    act('__style-blocked', { blocked: !!blocked });
    return () => act('__style-blocked', { blocked: false });
  }, [!!blocked]);
};
