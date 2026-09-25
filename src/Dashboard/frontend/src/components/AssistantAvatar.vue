<template>
  <span
    class="assistant-avatar"
    :class="{
      'assistant-avatar--thinking': thinking,
      'assistant-avatar--paused': paused,
    }"
    role="img"
    :aria-label="thinking ? 'Azure FinOps assistant, working' : 'Azure FinOps assistant'"
  >
    <svg width="32" height="32" viewBox="0 0 40 40" fill="none" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient :id="`${id}-surface`" x1="5" y1="3" x2="35" y2="39" gradientUnits="userSpaceOnUse">
          <stop stop-color="#23cce2" />
          <stop offset=".48" stop-color="#0078d4" />
          <stop offset="1" stop-color="#6654d9" />
        </linearGradient>
        <linearGradient :id="`${id}-spark`" x1="15" y1="12" x2="26" y2="29" gradientUnits="userSpaceOnUse">
          <stop stop-color="#fff" />
          <stop offset="1" stop-color="#bcefff" />
        </linearGradient>
      </defs>
      <rect x="1" y="1" width="38" height="38" rx="12" :fill="`url(#${id}-surface)`" />
      <rect x="1.5" y="1.5" width="37" height="37" rx="11.5" stroke="#fff" stroke-opacity=".3" />
      <g class="assistant-avatar-orbit">
        <ellipse cx="20" cy="20" rx="15" ry="8" transform="rotate(-35 20 20)" stroke="#d8f7ff" stroke-opacity=".55" stroke-width="1.2" />
        <circle cx="31" cy="11.5" r="2" fill="#e2fbff" />
      </g>
      <path
        d="M20 10.5C21.8 16.5 23.5 18.2 29.5 20C23.5 21.8 21.8 23.5 20 29.5C18.2 23.5 16.5 21.8 10.5 20C16.5 18.2 18.2 16.5 20 10.5Z"
        :fill="`url(#${id}-spark)`"
      />
    </svg>
  </span>
</template>

<script setup>
import { useId } from "vue";

defineProps({
  thinking: { type: Boolean, default: false },
  paused: { type: Boolean, default: false },
});

const id = `assistant-${useId()}`;
</script>

<style scoped>
.assistant-avatar {
  display: inline-flex;
  flex: 0 0 32px;
  width: 32px;
  height: 32px;
  border-radius: 10px;
  box-shadow: 0 2px 5px #0078d424;
  vertical-align: middle;
  line-height: 0;
}
svg {
  display: block;
  width: 100%;
  height: 100%;
}
.assistant-avatar-orbit {
  transform-box: view-box;
  transform-origin: center;
}
.assistant-avatar--thinking .assistant-avatar-orbit {
  animation: assistant-orbit 12s linear infinite;
}
.assistant-avatar--paused,
.assistant-avatar--paused *,
.assistant-avatar--paused .assistant-avatar-orbit {
  animation-play-state: paused;
  transition: none;
}
@keyframes assistant-orbit {
  to { transform: rotate(360deg); }
}
@media (prefers-reduced-motion: reduce) {
  .assistant-avatar--thinking .assistant-avatar-orbit {
    animation: none;
  }
  .assistant-avatar,
  .assistant-avatar * {
    transition: none;
  }
}
</style>
