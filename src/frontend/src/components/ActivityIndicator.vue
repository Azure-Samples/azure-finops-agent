<template>
  <div
    class="activity"
    :class="{ 'activity--paused': paused, 'activity--wrap': wrap }"
  >
    <span class="activity-dots" role="img" aria-label="Working"
      ><i></i><i></i><i></i
    ></span>
    <div class="activity-text">
      <div class="activity-line">
        <!-- Re-keyed so every new status replays the left-to-right reveal. -->
        <span v-if="label" :key="label" class="activity-label">{{
          label
        }}</span>
        <span v-if="elapsed" class="activity-elapsed" aria-hidden="true">{{
          elapsed
        }}</span>
        <slot />
      </div>
      <div v-if="detail" class="activity-detail">{{ detail }}</div>
    </div>
  </div>
</template>

<script setup>
// Live "the agent is working" row: three pulsing dots and a one-line status
// whose text sweeps in from the left. Status changes are not announced as a
// live region, so tool-by-tool updates never keep interrupting screen readers.
defineProps({
  label: { type: String, default: "" },
  elapsed: { type: String, default: "" },
  detail: { type: String, default: "" },
  paused: { type: Boolean, default: false },
  wrap: { type: Boolean, default: false },
});
</script>

<style scoped>
/* One status unit in the top bar's gradient: the dots take its three stops,
   the label is filled with it, and both share the label's line box so the
   dots sit on the text's centre line. */
.activity {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  min-width: 0;
  max-width: 100%;
  padding: 2px 0 8px;
  color: var(--accent);
  text-align: left;
}
.activity-dots {
  flex: 0 0 22px;
  height: var(--text-label-line);
  display: inline-flex;
  align-items: center;
  justify-content: space-between;
}
/* Static frame equals the animation's mid-pulse, so reduced motion and a
   paused tab still read as "working" rather than as an empty row. */
.activity-dots i {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--accent);
  opacity: 0.7;
  transform: translateY(-1.25px);
  animation: activity-pulse 1.2s linear infinite;
}
.activity-dots i:nth-child(1) {
  background: var(--brand-start);
  animation-delay: -408ms;
}
.activity-dots i:nth-child(2) {
  background: var(--brand-mid);
  animation-delay: -216ms;
}
.activity-dots i:nth-child(3) {
  background: var(--brand-end);
  animation-delay: -24ms;
}
.activity-text {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.activity-line {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  min-height: var(--text-label-line);
}
.activity-label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--text-label-size);
  line-height: var(--text-label-line);
  font-weight: 500;
  background: var(--brand-gradient);
  -webkit-background-clip: text;
  background-clip: text;
  -webkit-text-fill-color: transparent;
  color: transparent;
  -webkit-mask-image: linear-gradient(90deg, #000 33.333%, transparent 50%);
  mask-image: linear-gradient(90deg, #000 33.333%, transparent 50%);
  -webkit-mask-size: 300% 100%;
  mask-size: 300% 100%;
  -webkit-mask-repeat: no-repeat;
  mask-repeat: no-repeat;
  -webkit-mask-position: 0 0;
  mask-position: 0 0;
  animation: activity-reveal 350ms ease-out both;
}
.activity--wrap .activity-label {
  white-space: normal;
  overflow: visible;
}
.activity-elapsed {
  flex-shrink: 0;
  color: var(--text-muted);
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}
.activity-detail {
  margin-top: 2px;
  color: var(--text-muted);
  font-size: var(--text-caption-size);
  line-height: var(--text-caption-line);
  font-variant-numeric: tabular-nums;
}
.activity--paused .activity-dots i,
.activity--paused .activity-label {
  animation-play-state: paused;
  transition: none;
}
/* ((1 + cos 2πx) / 2)³ sampled every 5–10% and mapped to
   translateY(-2.5·p) scale(.85 + .3·p) opacity(.4 + .6·p). */
@keyframes activity-pulse {
  0%,
  10%,
  90%,
  100% {
    transform: translateY(0) scale(0.85);
    opacity: 0.4;
  }
  20%,
  80% {
    transform: translateY(-0.1px) scale(0.862);
    opacity: 0.425;
  }
  30%,
  70% {
    transform: translateY(-0.7px) scale(0.934);
    opacity: 0.568;
  }
  40%,
  60% {
    transform: translateY(-1.85px) scale(1.072);
    opacity: 0.844;
  }
  45%,
  55% {
    transform: translateY(-2.33px) scale(1.129);
    opacity: 0.958;
  }
  50% {
    transform: translateY(-2.5px) scale(1.15);
    opacity: 1;
  }
}
@keyframes activity-reveal {
  from {
    -webkit-mask-position: 100% 0;
    mask-position: 100% 0;
  }
  to {
    -webkit-mask-position: 0 0;
    mask-position: 0 0;
  }
}
@media (prefers-reduced-motion: reduce) {
  .activity-dots i,
  .activity-label {
    animation: none;
    transition: none;
  }
  .activity-label {
    -webkit-mask-image: none;
    mask-image: none;
  }
}
</style>
