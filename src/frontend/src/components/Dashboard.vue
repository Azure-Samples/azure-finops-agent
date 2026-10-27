<template>
  <div class="dashboard">
    <main class="main">
      <ChatView
        :user="user"
        @logout="$emit('logout')"
        @login="$emit('login')"
      />
    </main>
  </div>
</template>

<script setup>
import ChatView from "./ChatView.vue";

defineProps({
  user: { type: Object, default: null },
});

defineEmits(["logout", "login"]);
</script>

<style scoped>
.dashboard {
  display: flex;
  flex-direction: column;
  height: 100dvh;
  overflow: hidden;
}

.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}
</style>
