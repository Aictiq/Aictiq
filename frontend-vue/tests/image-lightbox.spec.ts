import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { nextTick } from 'vue'

import ImageLightbox from '@/components/common/ImageLightbox.vue'

function shown() {
  const content = document.querySelector('[data-testid="image-lightbox"]')
  return {
    src: content?.querySelector('img')?.getAttribute('src')?.replace(location.origin, '') ?? null,
    position: content?.querySelector('[data-testid="lightbox-position"]')?.textContent ?? null,
  }
}

async function settle() {
  await nextTick()
  await nextTick()
}

function key(name: string) {
  const content = document.querySelector('[data-testid="image-lightbox"]') as HTMLElement
  content.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true }))
}

function swipe(dx: number, dy = 0) {
  const content = document.querySelector('[data-testid="image-lightbox"]') as HTMLElement
  const at = (x: number, y: number) => ({ clientX: x, clientY: y }) as Touch
  content.dispatchEvent(
    Object.assign(new Event('touchstart', { bubbles: true }), { touches: [at(200, 200)] }),
  )
  content.dispatchEvent(
    Object.assign(new Event('touchend', { bubbles: true }), {
      changedTouches: [at(200 + dx, 200 + dy)],
    }),
  )
}

describe('ImageLightbox', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('steps through the images of the gallery with arrow keys and swipes', async () => {
    document.body.innerHTML = `
      <section data-image-gallery>
        <div class="aictiq-markdown" contenteditable="true"><img id="draft" src="/draft.png"></div>
        <div class="aictiq-markdown"><img id="a" src="/a.png" alt="A"></div>
        <div class="aictiq-markdown">
          <img id="b" src="/b.png" alt="B">
          <a href="/elsewhere"><img src="/linked.png"></a>
          <img id="c" src="/c.png" alt="C">
        </div>
      </section>
      <div class="aictiq-markdown"><img id="outside" src="/outside.png"></div>`
    const wrapper = mount(ImageLightbox, { attachTo: document.body })

    document.getElementById('b')!.click()
    await settle()
    expect(shown()).toEqual({ src: '/b.png', position: '2 / 3' })

    key('ArrowRight')
    await settle()
    expect(shown()).toEqual({ src: '/c.png', position: '3 / 3' })

    key('ArrowRight')
    await settle()
    expect(shown().src).toBe('/a.png')

    key('ArrowLeft')
    await settle()
    expect(shown().src).toBe('/c.png')

    swipe(-120)
    await settle()
    expect(shown().src).toBe('/a.png')

    swipe(120)
    await settle()
    expect(shown().src).toBe('/c.png')

    // Mostly vertical: a scroll, not a swipe.
    swipe(60, 200)
    await settle()
    expect(shown().src).toBe('/c.png')

    wrapper.unmount()
  })

  it('shows a lone image without gallery controls', async () => {
    document.body.innerHTML = `<div class="aictiq-markdown"><img id="only" src="/only.png"></div>`
    const wrapper = mount(ImageLightbox, { attachTo: document.body })

    document.getElementById('only')!.click()
    await settle()
    expect(shown()).toEqual({ src: '/only.png', position: null })
    expect(document.querySelector('[data-testid="lightbox-next"]')).toBeNull()

    wrapper.unmount()
  })
})
