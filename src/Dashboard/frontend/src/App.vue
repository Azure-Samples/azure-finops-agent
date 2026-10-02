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
  --font-sans:
    "Google Sans Flex", "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto,
    Helvetica, Arial, sans-serif;
  --font-mono: ui-monospace, "Cascadia Code", SFMono-Regular, Consolas,
    monospace;
  --font-variation-body: "ROND" 0, "wdth" 92;
  --font-variation-greeting: "ROND" 100, "wdth" 100;

  --bg: #ffffff;
  --surface: #ffffff;
  --primary: #0d0d0d;
  --ink: #1f1f1f;
  --text: #1f1f1f;
  --text-muted: #676767;
  --text-hint: #73777d;
  --user-bubble: #f4f4f4;
  --card: #f7f7f8;
  --border: #e5e5e5;
  --focus: #3678e8;
  --accent: #3678e8;
  --accent-hover: #245fbe;
  --danger: #d13438;
  --success: #1a7f37;
  --warning: #bf8700;

  --sidebar-bg: #fcfdff;
  --sidebar-border: rgba(44, 76, 124, 0.14);
  --sidebar-selected: rgba(44, 76, 124, 0.1);
  --sidebar-hover: rgba(44, 76, 124, 0.06);
  --sidebar-secondary: #3e4755;

  --chart-border: #dce5f4;
  --chart-shadow: rgba(37, 61, 103, 0.08);
  --activity-dot: #4b7ccd;

  --radius-composer: 32px;
  --radius-composer-mobile: 40px;
  --radius-bubble: 20px;
  --radius-card: 12px;
  --radius-chip: 14px;
  --radius-chart: 20px;
  --shadow-composer: 0 2px 10px rgba(0, 0, 0, 0.05);

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
  color: var(--text);
  line-height: var(--text-body-line);
  letter-spacing: 0;
  -webkit-font-smoothing: antialiased;
  font-size: var(--text-body-size);
  font-weight: 400;
}

button,
input,
select,
textarea {
  font-family: inherit;
  font-stretch: inherit;
  font-variation-settings: inherit;
}

.app {
  height: 100dvh;
  overflow: hidden;
}
</style>
