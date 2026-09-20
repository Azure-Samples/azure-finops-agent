<template>
  <section class="turn-failure" role="alert">
    <svg class="turn-failure-icon" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v6m0 3v1" />
    </svg>
    <div class="turn-failure-body">
      <h3>{{ failure.title }}</h3>
      <p>{{ failure.text }}</p>
      <p class="turn-failure-hint">{{ failure.hint }}</p>
      <template v-if="showEdit">
        <button type="button" :disabled="!canEdit" @click="$emit('edit')">Edit saved question</button>
        <p class="turn-failure-hint">Nothing is sent automatically. Your current draft will not be overwritten.</p>
      </template>
    </div>
  </section>
</template>

<script setup>
defineProps({
  failure: { type: Object, required: true },
  canEdit: { type: Boolean, default: false },
  showEdit: { type: Boolean, default: true },
});
defineEmits(["edit"]);
</script>

<style scoped>
.turn-failure {
  display: flex;
  gap: 12px;
  width: 100%;
  max-width: 760px;
  padding: 16px;
  border: 1px solid #edc7c9;
  border-left: 3px solid #a4262c;
  border-radius: 8px;
  background: #fff8f8;
  color: #323130;
  text-align: left;
}
.turn-failure-icon {
  flex-shrink: 0;
  color: #a4262c;
  margin-top: 2px;
}
.turn-failure-body {
  min-width: 0;
  overflow-wrap: anywhere;
}
h3 {
  margin: 0 0 6px;
  font-size: 15px;
  font-weight: 600;
}
p {
  margin: 0 0 10px;
  font-size: 14px;
}
.turn-failure-hint {
  font-size: 12px;
  color: #605e5c;
}
p:last-child {
  margin-bottom: 0;
}
button {
  margin-bottom: 8px;
  padding: 6px 12px;
  border: 1px solid #8a8886;
  border-radius: 4px;
  background: #fff;
  color: #323130;
  font: inherit;
  cursor: pointer;
}
button:hover:not(:disabled) {
  background: #f3f2f1;
}
button:focus-visible {
  outline: 2px solid #0078d4;
  outline-offset: 2px;
}
button:disabled {
  opacity: 0.5;
  cursor: default;
}
</style>
