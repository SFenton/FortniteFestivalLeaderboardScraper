---
status: canonical
owner: web
last_verified: 2026-10-03
last_verified_commit: fc9a7636
sources:
  - FortniteFestivalWeb/src/pages/settings/privacyPolicy.ts
  - FortniteFestivalWeb/src/pages/settings/PrivacyPolicyModal.tsx
  - FortniteFestivalWeb/src/pages/settings/SettingsPage.tsx
  - FortniteFestivalWeb/src/routes.ts
  - FortniteFestivalWeb/__test__/pages/settings/privacyPolicy.test.ts
  - FortniteFestivalWeb/src/api/client.ts
  - FSTService/Api/SelectedProfileActivityMiddleware.cs
  - FortniteFestivalWeb/src/pages/settings/feedback/FeedbackModal.tsx
update_triggers:
  - The privacy policy text, effective date, contact route, or client presentation changes.
  - A client or service change alters what data is collected, sent, shared, or retained.
---

# Privacy policy

This page holds the canonical text of the Festival Score Tracker privacy
policy. Every client (web, iOS, iPhone Duo, iPadOS, macOS, Android, and
Windows) must show the text between the markers below word for word, with the
same section order, headings, list items, and effective date. Only presentation
(fonts, list bullets, link styling) may differ by platform.

The web copy is `FortniteFestivalWeb/src/pages/settings/privacyPolicy.ts`.
`__test__/pages/settings/privacyPolicy.test.ts` renders that module to the
markdown form used here and fails if the two differ, so edit both together.
Native clients keep their own copy of this text and must be updated in the
same release when it changes.

When the text changes, update the effective date and add a What's New entry.
If a change in data handling (a new header, stored value, provider, or
retention rule) makes a statement below inaccurate, update the policy in the
same change.

## Policy text

<!-- privacy-policy:start -->
**Effective date:** October 3, 2026

Festival Score Tracker ("FST", "we", "us") is an independent, fan-made companion for Fortnite Festival. It is not affiliated with, endorsed by, or sponsored by Epic Games, Inc.

This policy explains what information the Festival Score Tracker website and apps handle, why we handle it, and the choices you have.

### Information we collect

You do not need an account to use Festival Score Tracker. We never ask for your name, email address, phone number, password, payment details, precise location, or contacts, and we only receive photos or videos that you choose to attach to feedback.

- **Public leaderboard data:** We collect publicly available Fortnite Festival leaderboard data from Epic Games, including Epic account IDs, display names, scores, ranks, accuracy, stars, full-combo status, band membership, and season.
- **Profiles you select:** When you select a player or band, the app sends its public Epic account IDs to our service so we can show its data, keep its score history up to date, and record when it was last viewed.
- **Technical data:** Like most online services, our servers receive standard request information such as IP address, device and browser type, the page or data requested, and the time of the request. If you export data, the request also includes your time zone so dates match your device.
- **Feedback you send:** If you use Report an Issue or Request a Feature, we receive the title, text, and any photos or videos you attach, along with the platform, app version, and device or browser details. We remove location and device metadata from attachments, may shrink or convert them so they fit, and file the report as an issue on GitHub.
- **Data on your device:** Your settings, selected profile, and display preferences are stored only on your device. They are not sent to us except as described above.

### How we use information

- To show leaderboards, rankings, statistics, rivals, suggestions, and score history.
- To preserve Fortnite Festival leaderboard history across seasonal resets.
- To operate, secure, troubleshoot, and improve the service, and to prevent abuse.
- To review, track, and respond to bug reports and feature requests you send.

We do not sell or rent personal information, use it for advertising, or track you across other companies' apps or websites.

### Third parties and data sources

- **Epic Games:** Leaderboard, song, and Item Shop data come from Epic Games services. Fortnite and Fortnite Festival are trademarks of Epic Games, Inc. Epic's handling of your Epic account is governed by Epic's own privacy policy.
- **Hosting and network providers:** Providers that host and deliver the service may process technical request data only as needed to carry that traffic.
- **GitHub:** Feedback you send is filed as an issue in our GitHub repository, where it may be publicly visible. Do not include private information. GitHub's handling of that content is governed by GitHub's own privacy policy.
- **External links:** Links such as the Fortnite Item Shop open websites operated by others, which have their own privacy policies.

We do not use third-party advertising or analytics services, and we do not share personal information with anyone else except as described above or when the law requires it.

### Data retention

- Public leaderboard data and score history are kept indefinitely, because preserving history across seasons is the purpose of the service.
- Technical request logs are kept only as long as needed to operate and secure the service, then deleted.
- Feedback stays on GitHub as an issue until we delete it. Our service deletes its temporary copies of your text and attachments once the issue is filed or processing fails.
- Data stored on your device stays there until you reset settings, clear the app or site data, or uninstall the app.

### Your choices and rights

- You can deselect a profile, export your data, or reset settings in the app at any time.
- To have feedback you sent edited or removed, contact us using the details below.
- Depending on where you live (for example, under the GDPR or the CCPA), you may have the right to access, correct, delete, or object to the processing of your personal information.
- To ask us to stop tracking your Epic account or remove its data from Festival Score Tracker, contact us using the details below. We may ask you to show that you control the account. Removing data from Festival Score Tracker does not change Epic's own leaderboards.

### Children

Festival Score Tracker is not directed to children under 13, and we do not knowingly collect personal information from them beyond the public leaderboard data Epic Games publishes.

### Security

All traffic to our service uses encrypted HTTPS connections, and access to our systems is restricted. No method of transmission or storage is completely secure, but we work to protect the information we hold.

### Changes to this policy

We may update this policy as the service changes. We will change the effective date above and mention significant changes in What's New.

### Contact us

For privacy questions or requests, open an issue at https://github.com/SFenton/FortniteFestivalLeaderboardScraper/issues. Issues are public, so do not include private information; we will follow up there to arrange anything else we need.
<!-- privacy-policy:end -->

## Presentation

| Platform | Entry point | Presentation |
|---|---|---|
| Web | Settings > Privacy Policy row and Settings Quick Links entry | Standard modal (bottom sheet on phones, centered dialog on larger screens) over Settings, deep-linkable at `/settings/privacy` (`/#/settings/privacy`) |
| iOS, iPhone Duo, iPadOS | Settings row | Sheet with a title, toolbar Done button, and swipe-down dismiss (page or form sheet on iPadOS) |
| macOS | Settings row | Sheet with a close button in the content |
| Android | Settings row | Full-screen dialog or bottom sheet with back/close |
| Windows | Settings row | ContentDialog or navigation page with back/close |

On the web, `/settings/privacy` renders the Settings page with the policy
modal open. Opening the row in the app pushes that route. Close, Escape, and
browser Back return to `/settings`, and Settings stays mounted with its scroll
position. A direct visit or refresh opens the same modal, and closing it
replaces the URL with `/settings`. Native clients render the policy with
native text views, not a WebView. The content scrolls, follows the platform's
text scaling, exposes headings and lists to screen readers, and stays inside
safe areas.
