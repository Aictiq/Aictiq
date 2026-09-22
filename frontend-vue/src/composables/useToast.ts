import { toast } from 'vue-sonner'

import { ApiError } from '@/utils/api'

/**
 * The app's one notification surface. Wrapping `vue-sonner` keeps the import in one
 * place and gives errors a single house style - in particular, an ApiError shows the
 * server's own `title` rather than a generic "something went wrong".
 */
export function useToast() {
  return {
    success: (message: string, description?: string) => toast.success(message, { description }),
    info: (message: string, description?: string) => toast(message, { description }),

    /**
     * Field-level validation belongs inline on the form, so a ValidationError here is
     * shown as its summary only - the form renders `fieldErrors` itself.
     */
    error: (error: unknown, fallback = 'Something went wrong.') => {
      if (error instanceof ApiError) {
        toast.error(error.title, { description: error.problem?.detail })
        return
      }
      toast.error(fallback)
    },
  }
}
