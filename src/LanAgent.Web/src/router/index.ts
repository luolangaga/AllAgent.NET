import { createRouter, createWebHashHistory } from 'vue-router'

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', redirect: '/chat' },
    {
      path: '/chat',
      component: () => import('@/views/ChatView.vue'),
    },
    {
      path: '/chat/:id',
      component: () => import('@/views/ChatView.vue'),
    },
    {
      path: '/skills',
      name: 'skills',
      component: () => import('@/views/SkillsView.vue'),
    },
    {
      path: '/settings',
      component: () => import('@/views/SettingsView.vue'),
    },
    {
      path: '/api-configs',
      component: () => import('@/views/ApiConfigView.vue'),
    },
    {
      path: '/mcp-configs',
      name: 'mcp-configs',
      component: () => import('@/views/McpConfigView.vue'),
    },
  ],
})

export default router
