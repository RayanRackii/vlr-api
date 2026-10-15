namespace Platform.Api.Modules.Rentals.Services;

internal static class TeacherLessonMessages
{
    public const string RentableNotFound = "A quadra não foi encontrada.";

    public const string SchedulePolicyMustBeSlotGrid = "A quadra precisa usar a grade de horários.";

    public const string FutureIntervalsOnly = "Aulas só podem alterar horários futuros.";

    public const string EndAfterStart = "O horário final precisa ser depois do inicial.";

    public const string TenantInactive = "A empresa não está ativa.";

    public const string TenantRequired = "O contexto da empresa é obrigatório.";

    public const string OverlapsExistingSlot = "O intervalo sobrepõe um horário existente.";

    public const string OverlapsNonOpenSlot =
        "O intervalo sobrepõe um horário que não está aberto ou não pode ser conferido.";

    public const string OverlapsExistingSlots = "O intervalo sobrepõe horários existentes.";

    public const string SlotStartTaken = "Já existe um horário neste início.";

    public const string WeeklyLessonNotFound = "Não há aula semanal neste intervalo.";

    public const string LessonSourceUnverifiable = "A origem da aula não pode ser conferida.";

    public const string DifferentWeeklyLessonWins = "Outra aula semanal passou a valer neste intervalo.";

    public const string OnlyExactLesson = "Só uma aula exata pode ser removida.";

    public const string OpenKindMissing = "O tipo de ocupação aberto não está disponível.";

    public const string LessonKindMissing = "O tipo de ocupação aula não está disponível.";

    public const string UnsafeKindFlags =
        "Os tipos de ocupação aberto e aula estão com configuração insegura.";

    public const string NotOpenWindow = "O intervalo não é uma janela aberta da grade.";

    public const string NotUniqueWindow = "O intervalo não corresponde a uma janela única da grade.";

    public const string OverlapsNonOpenTemplate = "O intervalo sobrepõe um modelo que não está aberto.";

    public const string OverlapsReservation = "O intervalo sobrepõe uma reserva.";
}
