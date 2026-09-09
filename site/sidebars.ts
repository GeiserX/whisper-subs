import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  docs: [
    'overview',
    {
      type: 'category',
      label: 'Getting started',
      collapsed: false,
      items: ['setup', 'choosing-a-variant', 'install-topologies'],
    },
    {
      type: 'category',
      label: 'Running it',
      collapsed: false,
      items: ['remote-workers'],
    },
    {
      type: 'category',
      label: 'Help',
      collapsed: false,
      items: ['diagnostics', 'limitations'],
    },
  ],
};

export default sidebars;
