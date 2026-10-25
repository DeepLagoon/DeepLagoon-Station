import { Component } from 'react';
import { createLogger } from '../logging';

const logger = createLogger('render');

export class InterfaceErrorBoundary extends Component {
  state = { failed: false, message: '' };

  static getDerivedStateFromError(error) {
    return { failed: true, message: String(error?.message ?? error).slice(0, 300) };
  }

  componentDidCatch(error, info) {
    logger.error('interface render error', error, info?.componentStack);
  }

  render() {
    if (!this.state.failed) return this.props.children;
    return <div role="alert" style={{ padding: '16px', color: '#ddd', background: '#171c24', height: '100%', overflow: 'auto' }}>
      <p>Не удалось отобразить интерфейс. / The window interface failed.</p>
      <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{this.state.message}</pre>
      <button type="button" onClick={() => location.reload()}>Перезагрузить окно / Reload window</button>
    </div>;
  }
}
