import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';
import {themes as prismThemes} from 'prism-react-renderer';

const config: Config = {
  title: 'WhisperSubs',
  tagline: 'Local AI subtitle generation for Jellyfin',
  favicon: 'img/favicon.ico',

  url: 'https://geiserx.github.io',
  baseUrl: '/whisper-subs/',
  organizationName: 'GeiserX',
  projectName: 'whisper-subs',

  // Emit <route>/index.html so GitHub Pages serves /docs/setup/ directly.
  // That URL is compiled into shipped plugin DLLs and must keep resolving.
  // trailingSlash intentionally unset: Docusaurus emits <route>/index.html by default

  onBrokenLinks: 'throw',
  onBrokenMarkdownLinks: 'warn',

  i18n: {defaultLocale: 'en', locales: ['en']},

  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/',
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/GeiserX/whisper-subs/tree/main/site/',
        },
        blog: false,
        theme: {customCss: './src/css/custom.css'},
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    image: 'img/social-preview.png',
    colorMode: {defaultMode: 'dark', respectPrefersColorScheme: true},
    navbar: {
      // No logo image: the project banner already contains the wordmark, so
      // pairing it with the title rendered "WhisperSubs" twice.
      title: 'WhisperSubs',
      items: [
        {type: 'docSidebar', sidebarId: 'docs', position: 'left', label: 'Documentation'},
        {href: 'https://github.com/GeiserX/whisper-subs', label: 'GitHub', position: 'right'},
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Documentation',
          items: [
            {label: 'Setup guide', to: '/docs/setup'},
            {label: 'Diagnostics', to: '/diagnostics'},
          ],
        },
        {
          title: 'Project',
          items: [
            {label: 'GitHub', href: 'https://github.com/GeiserX/whisper-subs'},
            {label: 'Issues', href: 'https://github.com/GeiserX/whisper-subs/issues'},
            {label: 'Releases', href: 'https://github.com/GeiserX/whisper-subs/releases'},
          ],
        },
      ],
      copyright: `WhisperSubs is free software under the GPL-3.0 licence. Built ${new Date().getFullYear()}.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['bash', 'json', 'yaml', 'csharp', 'docker'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
