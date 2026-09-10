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
      items: ['configuration', 'remote-workers'],
    },
    {
      type: 'category',
      label: 'Help',
      collapsed: false,
      items: ['diagnostics', 'limitations'],
    },
    {
      type: 'category',
      label: 'Reference',
      collapsed: false,
      items: ['api-and-internals'],
    },
  ],
};

export default sidebars;
