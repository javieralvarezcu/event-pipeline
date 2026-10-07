// Puente fino entre la página de Blazor (Calendar.razor) y FullCalendar.
// Los eventos llegan YA construidos desde C# (CalendarEventMapper); aquí solo
// se renderizan. eventClick y datesSet llaman de vuelta al componente.

function escapeHtml(text) {
  return String(text)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

window.calendarInterop = {
  /** Inicializa (o reinicializa) el calendario con los eventos dados. */
  render(el, eventsJson, dotnetRef) {
    try {
      if (typeof FullCalendar === 'undefined') return false;

      // "el" llega como el id del contenedor. FullCalendar acepta selectores CSS,
      // pero un id pelado ("calendar") no es un selector válido: resolver el elemento.
      const element = typeof el === 'string' ? document.getElementById(el) : el;
      if (!element) return false;

      const calendarEvents = JSON.parse(eventsJson);
      const currentDate = window.calendarInstance ? window.calendarInstance.getDate() : null;
      if (window.calendarInstance) {
        window.calendarInstance.destroy();
      }

      const calendar = new FullCalendar.Calendar(element, {
        initialView: 'dayGridMonth',
        initialDate: currentDate || undefined,
        locale: 'es',
        firstDay: 1, // la semana empieza en lunes
        headerToolbar: {
        left: 'prev,next today',
        center: 'title',
        right: 'dayGridMonth,listMonth'
      },
      buttonText: { today: 'Hoy', month: 'Mes', list: 'Lista' },
      displayEventEnd: false,
      events: calendarEvents.map((e) => ({
        title: e.title,
        start: e.start,
        end: e.end || undefined,
        allDay: true,
        backgroundColor: e.backgroundColor || undefined,
        borderColor: e.backgroundColor || undefined,
        startRecur: e.startRecur || undefined,
        endRecur: e.endRecur || undefined,
        daysOfWeek: e.daysOfWeek || undefined,
        extendedProps: { kind: e.kind, key: e.key, subtitle: e.subtitle }
      })),
      datesSet: (info) => {
        if (!dotnetRef) return;
        // El mes mostrado = el del punto medio del rango visible (el inicio del
        // rango puede caer en el mes anterior por el primer día de la semana).
        const mid = new Date((info.start.getTime() + info.end.getTime()) / 2);
        const currentMonth = `${mid.getFullYear()}-${String(mid.getMonth() + 1).padStart(2, '0')}`;
        dotnetRef.invokeMethodAsync('OnMonthChanged', currentMonth);
      },
      eventClick: (info) => {
        if (!dotnetRef) return;
        dotnetRef.invokeMethodAsync('OnEventClick', info.event.extendedProps.kind, info.event.extendedProps.key);
      },
      eventContent: (arg) => {
        const lines = [escapeHtml(arg.event.title)];
        if (arg.event.extendedProps.subtitle) {
          lines.push(escapeHtml(arg.event.extendedProps.subtitle));
        }
        return { html: lines.join('<br>') };
      }
    });

      calendar.render();
      window.calendarInstance = calendar;
      return true;
    } catch (error) {
      // Cualquier fallo (JSON, FullCalendar…) se informa como false para que la
      // página muestre el banner de error en vez de un circuito roto.
      console.error('calendarInterop.render falló:', error);
      return false;
    }
  },

  destroy() {
    if (window.calendarInstance) {
      window.calendarInstance.destroy();
      window.calendarInstance = null;
    }
  }
};
