// Posts a short release announcement in the Discord server through a channel webhook.
// Run by .github/workflows/announce-release.yml.
const {
  DISCORD_WEBHOOK = '',
  DISCORD_ROLE_ID = '',
  TAG = '',
  REPOSITORY = '',
  GITHUB_TOKEN = '',
  DRY_RUN = 'false',
} = process.env;

const fail = (message) => {
  console.error(message);
  process.exit(1);
};

if (!/^v\d+\.\d+\.\d+$/.test(TAG)) fail(`Not a release tag: "${TAG}"`);
if (DISCORD_ROLE_ID && !/^\d{17,20}$/.test(DISCORD_ROLE_ID)) fail('DISCORD_RELEASE_ROLE_ID is not a Discord role ID');

const releaseResponse = await fetch(`https://api.github.com/repos/${REPOSITORY}/releases/tags/${TAG}`, {
  headers: {
    Accept: 'application/vnd.github+json',
    'User-Agent': 'ryusak-release-announcer',
    ...(GITHUB_TOKEN ? { Authorization: `Bearer ${GITHUB_TOKEN}` } : {}),
  },
});
if (!releaseResponse.ok) fail(`There is no published release for ${TAG} (GitHub answered ${releaseResponse.status})`);
const release = await releaseResponse.json();
if (release.draft || release.prerelease) fail(`${TAG} is a draft or a pre-release, so it is not announced`);

const version = TAG.slice(1);
const ping = DISCORD_ROLE_ID ? `<@&${DISCORD_ROLE_ID}> ` : '';
const content = [
  `${ping}**RyuSAK ${version} is out!**`,
  'On Windows, Steam Deck and Linux, open RyuSAK and choose **Update Now**. On a Mac, download the new DMG.',
  `Release notes and downloads: ${release.html_url}`,
].join('\n');

const webhookUrl = () => {
  if (!DISCORD_WEBHOOK) fail('The DISCORD_RELEASE_WEBHOOK secret is not set');
  const url = new URL(DISCORD_WEBHOOK);
  if (!/(^|\.)discord(app)?\.com$/.test(url.hostname) || !url.pathname.startsWith('/api/webhooks/')) {
    fail('DISCORD_RELEASE_WEBHOOK is not a Discord webhook URL');
  }
  return url;
};

if (DRY_RUN === 'true') {
  console.log(content);
  const check = await fetch(webhookUrl());
  if (!check.ok) fail(`The webhook did not answer (${check.status})`);
  const hook = await check.json();
  console.log(`Dry run, nothing was posted. The webhook "${hook.name}" posts in channel ${hook.channel_id}.`);
  process.exit(0);
}

const url = webhookUrl();
url.searchParams.set('wait', 'true');
const post = await fetch(url, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    content,
    allowed_mentions: { parse: [], roles: DISCORD_ROLE_ID ? [DISCORD_ROLE_ID] : [] },
  }),
});
if (!post.ok) fail(`Discord did not accept the announcement (${post.status}): ${await post.text()}`);
const message = await post.json();
console.log(`Posted message ${message.id} in channel ${message.channel_id}.`);
if (DISCORD_ROLE_ID && !(message.mention_roles || []).includes(DISCORD_ROLE_ID)) {
  fail('The announcement was posted, but the role was not pinged');
}
