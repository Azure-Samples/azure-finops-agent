<template>
  <div class="app">
    <Dashboard :user="user" @logout="logout" @login="checkAuth" />
  </div>
</template>

<script setup>
import { onMounted, ref } from "vue";
import Dashboard from "./components/Dashboard.vue";

const user = ref(null);

async function checkAuth() {
  try {
    const res = await fetch("/auth/me");
    if (res.ok) {
      user.value = await res.json();
    }
  } catch {}
}

onMounted(checkAuth);

async function logout() {
  await fetch("/auth/logout", { method: "POST" });
  user.value = null;
}
</script>

<style>
/* Google Sans Flex (SIL OFL 1.1, see assets/fonts/OFL.txt). Self-hosted: the
   CSP only allows same-origin fonts. One variable file covers wght 300-700
   and wdth 92-100. */
@font-face {
  font-family: "Google Sans Flex";
  src: url("./assets/fonts/GoogleSansFlex.ttf") format("truetype");
  font-weight: 300 700;
  font-stretch: 92% 100%;
  font-style: normal;
  font-display: swap;
}

*,
*::before,
*::after {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

:root {
  /* One font for every piece of text in the app, code included; sizes,
     weights and colours vary, the family and its width never do. */
  --font-sans:
    "Google Sans Flex", "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto,
    Helvetica, Arial, sans-serif;
  --font-variation-body: "ROND" 0, "wdth" 92;

  /* One palette: a white page, one tint for side panels and quiet fills,
     one hover, one selection and one border colour. */
  --bg: #ffffff;
  --surface: #ffffff;
  --tint: #f5f7fa;
  --hover: rgba(31, 51, 82, 0.06);
  --selected: rgba(31, 51, 82, 0.1);
  --border: rgba(31, 51, 82, 0.14);
  --backdrop: rgba(15, 23, 42, 0.4);
  --code-bg: #1e1e1e;

  --ink: #1f1f1f;
  --text-muted: #5f6672;

  /* One accent for every primary action, link, focus ring and selection,
     and one soft fill per status. */
  --accent: #3678e8;
  --accent-hover: #245fbe;
  --accent-soft: rgba(54, 120, 232, 0.1);
  --accent-soft-strong: rgba(54, 120, 232, 0.16);
  --danger: #d13438;
  --danger-soft: rgba(209, 52, 56, 0.08);
  --success: #1a7f37;
  --success-soft: rgba(26, 127, 55, 0.1);
  --warning: #bf8700;
  --warning-soft: rgba(191, 135, 0, 0.1);

  /* Two corner sizes: --radius everywhere, --radius-lg for dialogs, the
     composer and your message bubble. Indicators and avatars stay round. */
  --radius: 8px;
  --radius-lg: 12px;

  /* One shadow, only for things that float: dialogs, popovers, the menu. */
  --shadow: 0 8px 24px rgba(15, 23, 42, 0.12);

  --text-body-size: 17px;
  --text-body-line: 26px;
  --text-label-size: 15px;
  --text-label-line: 22px;
  --text-caption-size: 13px;
  --text-caption-line: 18px;
  --text-title-size: 19px;
  --text-title-line: 26px;
  --text-heading-size: 23px;
  --text-heading-line: 30px;

  --motion-fast: 150ms ease-out;
  --motion-enter: 300ms ease-out;
  --motion-collapse: 350ms cubic-bezier(0.33, 1, 0.68, 1);
}

body {
  font-family: var(--font-sans);
  font-variation-settings: var(--font-variation-body);
  font-stretch: 92%;
  background: var(--bg);
  color: var(--ink);
  line-height: var(--text-body-line);
  letter-spacing: 0;
  -webkit-font-smoothing: antialiased;
  font-size: var(--text-body-size);
  font-weight: 400;
}

button,
input,
select,
textarea,
code,
pre,
kbd,
samp {
  font-family: inherit;
  font-stretch: inherit;
  font-variation-settings: inherit;
}

.app {
  height: 100dvh;
  overflow: hidden;
}
</style>
