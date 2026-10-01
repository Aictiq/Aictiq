import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { listNotifications, markNotificationsRead, type Notification } from '@/api/notifications'
import { useSessionStore } from '@/stores/session'
import { useToast } from '@/composables/useToast'

export function useNotifications() {
  const client = useQueryClient()
  const session = useSessionStore()
  const toast = useToast()
  const notifications = useQuery({
    queryKey: ['notifications'],
    queryFn: () => listNotifications(),
  })
  const markRead = useMutation({
    mutationFn: ({ ids, all = false }: { ids?: string[]; all?: boolean }) =>
      markNotificationsRead(ids, all),
    onSuccess: async (result, { ids, all }) => {
      await client.cancelQueries({ queryKey: ['notifications'] })
      const now = new Date().toISOString()
      client.setQueryData<Notification[]>(['notifications'], (rows) =>
        rows?.map((entry) =>
          !entry.readAt && (all || ids?.includes(entry.id)) ? { ...entry, readAt: now } : entry,
        ),
      )
      session.apply({
        unreadCount: all ? 0 : Math.max(0, (session.user?.unreadCount ?? 0) - result.read),
      })
      await Promise.all([client.invalidateQueries({ queryKey: ['notifications'] }), session.load()])
    },
    onError: (error) => toast.error(error, 'Notifications could not be marked read.'),
  })
  return { notifications, markRead }
}
