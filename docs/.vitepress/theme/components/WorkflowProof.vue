<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { createLazyMounter } from '../../../../apps/web/src/components/landing/createLazyMounter'

const host = ref<HTMLElement | null>(null)
let dispose: (() => void) | undefined

onMounted(() => {
  if (!host.value) return
  // Only load and mount the heavy React player once the host nears the viewport;
  // createLazyMounter owns the IntersectionObserver and teardown race-safety.
  dispose = createLazyMounter({
    host: host.value,
    load: async () => {
      const mod = await import('../../../../apps/web/src/components/LandingWorkflowDemo')
      return mod.mountLandingWorkflowDemo
    },
  })
})

onBeforeUnmount(() => {
  dispose?.()
  dispose = undefined
})
</script>

<template>
  <div ref="host" class="aw-workflow-proof" />
</template>
