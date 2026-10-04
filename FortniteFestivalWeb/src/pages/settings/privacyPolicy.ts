/**
 * Festival Score Tracker privacy policy.
 *
 * `docs/reference/privacy-policy.md` is the canonical copy shared with every
 * client; a unit test keeps this module identical to it.
 */
export type PrivacyPolicyItem = {
  label?: string;
  text: string;
};

export type PrivacyPolicySection = {
  id: string;
  heading: string;
  paragraphs?: readonly string[];
  items?: readonly PrivacyPolicyItem[];
  closing?: readonly string[];
};

export type PrivacyPolicy = {
  title: string;
  effectiveDate: string;
  intro: readonly string[];
  sections: readonly PrivacyPolicySection[];
};

export const PRIVACY_POLICY_CONTACT_URL = 'https://github.com/SFenton/FortniteFestivalLeaderboardScraper/issues';

export const privacyPolicy: PrivacyPolicy = {
  title: 'Privacy Policy',
  effectiveDate: 'October 3, 2026',
  intro: [
    'Festival Score Tracker ("FST", "we", "us") is an independent, fan-made companion for Fortnite Festival. It is not affiliated with, endorsed by, or sponsored by Epic Games, Inc.',
    'This policy explains what information the Festival Score Tracker website and apps handle, why we handle it, and the choices you have.',
  ],
  sections: [
    {
      id: 'information-we-collect',
      heading: 'Information we collect',
      paragraphs: [
        'You do not need an account to use Festival Score Tracker. We never ask for your name, email address, phone number, password, payment details, precise location, or contacts, and we only receive photos or videos that you choose to attach to feedback.',
      ],
      items: [
        {
          label: 'Public leaderboard data',
          text: 'We collect publicly available Fortnite Festival leaderboard data from Epic Games, including Epic account IDs, display names, scores, ranks, accuracy, stars, full-combo status, band membership, and season.',
        },
        {
          label: 'Profiles you select',
          text: 'When you select a player or band, the app sends its public Epic account IDs to our service so we can show its data, keep its score history up to date, and record when it was last viewed.',
        },
        {
          label: 'Technical data',
          text: 'Like most online services, our servers receive standard request information such as IP address, device and browser type, the page or data requested, and the time of the request. If you export data, the request also includes your time zone so dates match your device.',
        },
        {
          label: 'Feedback you send',
          text: 'If you use Report an Issue or Request a Feature, we receive the title, text, and any photos or videos you attach, along with the platform, app version, and device or browser details. We remove location and device metadata from attachments, may shrink or convert them so they fit, and file the report as an issue on GitHub.',
        },
        {
          label: 'Data on your device',
          text: 'Your settings, selected profile, and display preferences are stored only on your device. They are not sent to us except as described above.',
        },
      ],
    },
    {
      id: 'how-we-use-information',
      heading: 'How we use information',
      items: [
        { text: 'To show leaderboards, rankings, statistics, rivals, suggestions, and score history.' },
        { text: 'To preserve Fortnite Festival leaderboard history across seasonal resets.' },
        { text: 'To operate, secure, troubleshoot, and improve the service, and to prevent abuse.' },
        { text: 'To review, track, and respond to bug reports and feature requests you send.' },
      ],
      closing: [
        'We do not sell or rent personal information, use it for advertising, or track you across other companies\' apps or websites.',
      ],
    },
    {
      id: 'third-parties',
      heading: 'Third parties and data sources',
      items: [
        {
          label: 'Epic Games',
          text: 'Leaderboard, song, and Item Shop data come from Epic Games services. Fortnite and Fortnite Festival are trademarks of Epic Games, Inc. Epic\'s handling of your Epic account is governed by Epic\'s own privacy policy.',
        },
        {
          label: 'Hosting and network providers',
          text: 'Providers that host and deliver the service may process technical request data only as needed to carry that traffic.',
        },
        {
          label: 'GitHub',
          text: 'Feedback you send is filed as an issue in our GitHub repository, where it may be publicly visible. Do not include private information. GitHub\'s handling of that content is governed by GitHub\'s own privacy policy.',
        },
        {
          label: 'External links',
          text: 'Links such as the Fortnite Item Shop open websites operated by others, which have their own privacy policies.',
        },
      ],
      closing: [
        'We do not use third-party advertising or analytics services, and we do not share personal information with anyone else except as described above or when the law requires it.',
      ],
    },
    {
      id: 'retention',
      heading: 'Data retention',
      items: [
        { text: 'Public leaderboard data and score history are kept indefinitely, because preserving history across seasons is the purpose of the service.' },
        { text: 'Technical request logs are kept only as long as needed to operate and secure the service, then deleted.' },
        { text: 'Feedback stays on GitHub as an issue until we delete it. Our service deletes its temporary copies of your text and attachments once the issue is filed or processing fails.' },
        { text: 'Data stored on your device stays there until you reset settings, clear the app or site data, or uninstall the app.' },
      ],
    },
    {
      id: 'your-choices',
      heading: 'Your choices and rights',
      items: [
        { text: 'You can deselect a profile, export your data, or reset settings in the app at any time.' },
        { text: 'To have feedback you sent edited or removed, contact us using the details below.' },
        { text: 'Depending on where you live (for example, under the GDPR or the CCPA), you may have the right to access, correct, delete, or object to the processing of your personal information.' },
        { text: 'To ask us to stop tracking your Epic account or remove its data from Festival Score Tracker, contact us using the details below. We may ask you to show that you control the account. Removing data from Festival Score Tracker does not change Epic\'s own leaderboards.' },
      ],
    },
    {
      id: 'children',
      heading: 'Children',
      paragraphs: [
        'Festival Score Tracker is not directed to children under 13, and we do not knowingly collect personal information from them beyond the public leaderboard data Epic Games publishes.',
      ],
    },
    {
      id: 'security',
      heading: 'Security',
      paragraphs: [
        'All traffic to our service uses encrypted HTTPS connections, and access to our systems is restricted. No method of transmission or storage is completely secure, but we work to protect the information we hold.',
      ],
    },
    {
      id: 'changes',
      heading: 'Changes to this policy',
      paragraphs: [
        'We may update this policy as the service changes. We will change the effective date above and mention significant changes in What\'s New.',
      ],
    },
    {
      id: 'contact',
      heading: 'Contact us',
      paragraphs: [
        `For privacy questions or requests, open an issue at ${PRIVACY_POLICY_CONTACT_URL}. Issues are public, so do not include private information; we will follow up there to arrange anything else we need.`,
      ],
    },
  ],
};
