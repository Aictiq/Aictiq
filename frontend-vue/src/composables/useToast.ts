import { toast } from 'vue-sonner'

import { ApiError } from '@/utils/api'

/**
 * A save confirmation is an acknowledgement, not news: it has to be seen, not read, so it
 * leaves on its own well before the default lifetime. Failures keep the default - their
 * reason has to stay on screen long enough to be read.
 */
const savedDuration = 2000

/**
 * News about what someone else did arrives unasked, so it gets its own corner: the top
 * right, away from the bottom-right stack where the page answers the viewer's own actions.
 */
export const activityToasterId = 'activity'

/**
 * The app's one notification surface. Wrapping `vue-sonner` keeps the import in one
 * place and gives errors a single house style - in particular, an ApiError shows the
 * server's own `title` rather than a generic "something went wrong".
 */
export function useToast() {
  /**
   * Field-level validation belongs inline on the form, so a ValidationError here is
   * shown as its summary only - the form renders `fieldErrors` itself.
   */
  const error = (error: unknown, fallback = 'Something went wrong.') => {
    if (error instanceof ApiError) {
      toast.error(error.title, { description: error.problem?.detail })
      return
    }
    toast.error(fallback)
  }

  return {
    success: (message: string, description?: string) => toast.success(message, { description }),
    info: (message: string, description?: string) => toast(message, { description }),
    /** Someone else's change to what is on screen - shown in the activity corner. */
    activity: (message: string, description?: string) =>
      toast(message, { description, toasterId: activityToasterId }),
    error,

    /** Every "it went through" for a save, so they all confirm alike and leave alike. */
    saved: (message = 'Saved.', description?: string) =>
      toast.success(message, { description, duration: savedDuration }),

    /** The other half of a save: what went wrong, in the server's own words when it said. */
    saveFailed: (reason: unknown, fallback = 'Changes could not be saved.') =>
      error(reason, fallback),
  }
}
