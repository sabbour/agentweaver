import { BrowserRouter, Link, Route, Routes } from 'react-router-dom';
import {
  FluentProvider,
  Title1,
  makeStyles,
  tokens,
  webLightTheme,
} from '@fluentui/react-components';
import { HomePage } from './pages/HomePage';
import { WatchPage } from './pages/WatchPage';

const useStyles = makeStyles({
  app: {
    minHeight: '100vh',
    backgroundColor: tokens.colorNeutralBackground2,
  },
  header: {
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalXXL}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  brand: {
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
  },
  main: {
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
    maxWidth: '960px',
    margin: '0 auto',
  },
});

function Shell() {
  const styles = useStyles();
  return (
    <div className={styles.app}>
      <header className={styles.header}>
        <Link to="/" className={styles.brand}>
          <Title1>Scaffolder</Title1>
        </Link>
      </header>
      <main className={styles.main}>
        <Routes>
          <Route path="/" element={<HomePage />} />
          <Route path="/watch/:runId" element={<WatchPage />} />
        </Routes>
      </main>
    </div>
  );
}

function App() {
  return (
    <FluentProvider theme={webLightTheme}>
      <BrowserRouter>
        <Shell />
      </BrowserRouter>
    </FluentProvider>
  );
}

export default App;
