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
  --bg: #ffffff;
  --surface: #ffffff;
  --border: #e5e5e5;
  --text: #1f1f1f;
  --text-muted: #676767;
  --accent: #0078d4;
  --accent-hover: #106ebe;
  --green: #107c10;
  --red: #d13438;
  --azure-blue: #0078d4;
  --azure-dark-blue: #005a9e;
  --azure-header: #0078d4;
}

body {
  font-family:
    "Google Sans Flex",
    "Segoe UI",
    -apple-system,
    BlinkMacSystemFont,
    Roboto,
    Helvetica,
    Arial,
    sans-serif;
  font-variation-settings: "ROND" 0;
  font-stretch: 92%;
  background: var(--bg);
  color: var(--text);
  line-height: 1.5;
  letter-spacing: 0;
  -webkit-font-smoothing: antialiased;
  font-size: 14px;
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
