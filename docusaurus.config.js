// @ts-check
// `@type` JSDoc annotations allow editor autocompletion and type checking
// (when paired with `@ts-check`).
// There are various equivalent ways to declare your Docusaurus config.
// See: https://docusaurus.io/docs/api/docusaurus-config


/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'Ed-Fi API Publisher',
  tagline: 'A tool to synchronize Ed-Fi ODS/API instances',
  favicon: 'img/tech-congress-favicon.png',

  // Set the production url of your site here
  url: 'https://ed-fi-api-publisher.github.io',
  // Set the /<baseUrl>/ pathname under which your site is served
  // For GitHub pages deployment, it is often '/<projectName>/'
  baseUrl: '/Ed-Fi-API-Publisher/',

  // GitHub pages deployment config.
  // If you aren't using GitHub pages, you don't need these.
  organizationName: 'ed-fi-alliance-oss', // Usually your GitHub org/user name.
  projectName: 'Ed-Fi-API-Publisher', // Usually your repo name.

  onBrokenLinks: 'throw',
  onBrokenMarkdownLinks: 'warn',

  // Even if you don't use internationalization, you can use this field to set
  // useful metadata like html lang. For example, if your site is Chinese, you
  // may want to replace "en" with "zh-Hans".
  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },
  markdown: {
    mermaid: true,
  },
  themes: ['@docusaurus/theme-mermaid'],
  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          sidebarPath: './sidebars.js',
          // Please change this to your repo.
          // Remove this to remove the "edit this page" links.
          editUrl:
            'https://github.com/ed-fi-alliance-oss/Ed-Fi-API-Publisher/tree/gh-pages/pages/docs/',
        },
        theme: {
          customCss: './src/css/custom.css',
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      // Replace with your project's social card
      image: 'img/ed-fi-social-card.jpg',
      navbar: {
        //title: 'Ed-Fi Alliance ',
        logo: {
          alt: 'Ed-Fi Docs Logo',
          src: 'img/ed-fi-logo.webp',
          srcDark: 'img/ed-fi-logo-light.webp',
        },
        items: [
          {to: 'https://ed-fi-alliance-oss.github.io/blog', label: 'Developer Blog', position: 'left'},
          {
            type: 'docSidebar',
            sidebarId: 'tutorialSidebar',
            position: 'left',
            label: 'API Publisher Docs',
          },
          {
            href: 'https://community.ed-fi.org/',
            label: 'Community',
            position: 'right',
          },
          {
            href: 'https://academy.ed-fi.org/',
            label: 'Academy',
            position: 'right',
          },
        ],
      },
      algolia: {
        // The application ID provided by Algolia
        appId: 'HFTFG7KSKA',

        // Public API key: it is safe to commit it
        apiKey: 'a58d193c70793d68151589d3cfbdeb6b',

        indexName: 'ed-fi-alliance-ossio',

        // Optional: see doc section below
        contextualSearch: true,

        // Optional: Specify domains where the navigation should occur through window.location instead on history.push. Useful when our Algolia config crawls multiple documentation sites and we want to navigate with window.location.href to them.
        externalUrlRegex: 'external\\.com|domain\\.com',

        // Optional: Replace parts of the item URLs from Algolia. Useful when using the same search index for multiple deployments using a different baseUrl. You can use regexp or string in the `from` param. For example: localhost:3000 vs myCompany.com/docs
        replaceSearchResultPathname: {
          from: '/docs/', // or as RegExp: /\/docs\//
          to: '/',
        },

        // Optional: Algolia search parameters
        searchParameters: {},

        // Optional: path for search page that enabled by default (`false` to disable it)
        searchPagePath: 'search',

        placeholder: 'Ask me something',

        //... other Algolia params
        headerLinks: [
          { search: true }
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Tech',
            items: [
              {
                label: 'Tutorial',
                to: 'https://ed-fi-alliance-oss.github.io/docs/intro',
              },
              {
                label: 'Data Exchange Standards',
                to: 'https://ed-fi-alliance-oss.github.io/docs/data-exchange',
              },
              {
                label: 'Technology Suite',
                to: 'https://ed-fi-alliance-oss.github.io/docs/technology-suite',
              },
            ],
          },
          {
            title: 'Resources',
            items: [
              {
                href: 'https://community.ed-fi.org/',
                label: 'Community Hub',
                position: 'right',
              },
              {
                href: 'https://techdocs.ed-fi.org',
                label: 'Tech Docs (legacy)',
                position: 'right',
              },
              {
                href: 'https://academy.ed-fi.org/',
                label: 'Academy',
                position: 'right',
              },
              {
                href: 'https://exchange.ed-fi.org/',
                label: 'Exchange',
                position: 'right',
              },
            ],
          },
          {
            title: 'More',
            items: [
              {
                label: 'Ed-Fi Alliance',
                to: 'https://www.ed-fi.org',
              },
              {
                label: 'Ed-Fi News',
                to: 'https://www.ed-fi.org/blog',
              },
              {
                label: 'Ed-Fi Events',
                href: 'https://www.ed-fi.org/events/',
              },
            ],
          },
        ],
        copyright: `Copyright © ${new Date().getFullYear()} Ed-Fi Alliance.`,
      },
    }),
    headTags: [
      {
        tagName: 'link',
        attributes: {
          rel: 'preconnect',
          href: 'https://fonts.googleapis.com',
        },
      },
      {
        tagName: 'link',
        attributes: {
          rel: 'preconnect',
          href: 'https://fonts.gstatic.com',
          crossorigin: 'anonymous'
        },
      },
      {
        tagName: 'link',
        attributes: {
          rel: 'stylesheet',
          href: 'https://fonts.googleapis.com/css2?family=Montserrat:ital,wght@0,100..900;1,100..900&display=swap',
        },
      }
    ],
    stylesheets: [
      {
          href: "https://unpkg.com/@antonz/codapi@0.19.7/dist/snippet.css",
      },
    ],
    scripts: [
        {
            src: "https://unpkg.com/@antonz/codapi@0.19.7/dist/snippet.js",
            defer: true,
        },
    ],
};

export default config;
