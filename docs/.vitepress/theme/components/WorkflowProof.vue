<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

const host = ref<HTMLElement | null>(null)
let unmount: (() => void) | undefined

onMounted(async () => {
  if (!host.value) return
  const { mountLandingWorkflowDemo } = await import(
    '../../../../apps/web/src/components/LandingWorkflowDemo'
  )
  unmount = mountLandingWorkflowDemo(host.value)
})

onBeforeUnmount(() => {
  unmount?.()
  unmount = undefined
})
</script>

<template>
  <div ref="host" class="aw-workflow-proof" />
</template>
