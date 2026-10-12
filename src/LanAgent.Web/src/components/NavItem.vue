<template>
  <RouterLink :to="to" class="nav-item" :class="{ active: isActive }">
    <span class="nav-icon">
      <svg v-if="icon === 'skills'" width="18" height="18" viewBox="0 0 18 18" fill="none">
        <path d="M7 2.5A4.5 4.5 0 002.5 7c0 1.8 1.06 3.36 2.6 4.1L2.5 15.5h3l1.5-2.5h2l1.5 2.5h3l-2.6-4.4A4.5 4.5 0 007 2.5z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
        <circle cx="12.5" cy="5.5" r="2" stroke="currentColor" stroke-width="1.2"/>
      </svg>
      <svg v-else-if="icon === 'api'" width="18" height="18" viewBox="0 0 18 18" fill="none">
        <path d="M9 2a7 7 0 100 14A7 7 0 009 2z" stroke="currentColor" stroke-width="1.2"/>
        <path d="M5.5 9h7M9 5.5v7" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
      </svg>
      <svg v-else-if="icon === 'settings'" width="18" height="18" viewBox="0 0 18 18" fill="none">
        <circle cx="9" cy="9" r="2.5" stroke="currentColor" stroke-width="1.2"/>
        <path d="M9 1.5v2M9 14.5v2M1.5 9h2M14.5 9h2M3.7 3.7l1.4 1.4M12.9 12.9l1.4 1.4M3.7 14.3l1.4-1.4M12.9 5.1l1.4-1.4" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
      </svg>
    </span>
    <span v-if="!collapsed" class="nav-label">{{ label }}</span>
  </RouterLink>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'

const props = defineProps<{
  icon: string
  label: string
  to: string
  collapsed: boolean
}>()

const route = useRoute()
const isActive = computed(() => route.path.startsWith(props.to))
</script>

<style scoped>
.nav-label { white-space: nowrap; overflow: hidden; }
</style>
