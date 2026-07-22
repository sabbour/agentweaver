<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { createLazyMounter } from '../../../../apps/web/src/components/landing/createLazyMounter'

const host = ref<HTMLElement | null>(null)
let dispose: (() => void) | undefined

onMounted(() => {
  if (!host.value) return
  dispose = createLazyMounter({
    host: host.value,
    load: async () => {
      const mod = await import('../../../../apps/web/src/components/LandingWorkflowEditorDemo')
      return mod.mountLandingWorkflowEditorDemo
    },
  })
})

onBeforeUnmount(() => {
  dispose?.()
  dispose = undefined
})
</script>

<template>
  <div ref="host" class="aw-workflow-editor-proof" />
</template>
