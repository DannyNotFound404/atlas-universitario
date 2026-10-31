namespace Miguel.Server.Storage;

public sealed class EnrollmentRejectedException : Exception
{
    public EnrollmentRejectedException()
        : base("El token de inscripción no es válido, ya fue utilizado o expiró.")
    {
    }
}
