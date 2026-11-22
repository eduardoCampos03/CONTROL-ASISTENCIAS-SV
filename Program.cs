using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

string connectionString = "Server=db81265.databaseasp.net; Database=db81265; User Id=db81265; Password=Asistencia2026Sv; Encrypt=True; TrustServerCertificate=True; MultipleActiveResultSets=True;";
string googleScriptUrl = "https://script.google.com/macros/s/AKfycbwSJ4abRmc8j6iyX7fXNHeD7VaCBhlMRhgGLeH1fYAwueHX5AT1x3Jqpo4V4r1fo_74/exec";

// Credenciales de Administrador
var administradores = new Dictionary<string, string>(StringComparer.Ordinal)
{
    { "admin", "Admin2026Sv" },
    { "Angelasofia", "CarlosyangelaForever" }
};

// Grados académicos oficiales permitidos
var gradosPermitidos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
{
    { "bachillerato", 1 },
    { "tecnico", 2 },
    { "universidad", 3 },
    { "posgrado", 4 }
};

app.MapPost("/api/login", (LoginRequest req) =>
{
    string usuarioIngresado = req.Usuario?.Trim() ?? "";
    if (administradores.TryGetValue(usuarioIngresado, out string? passValida) && passValida == req.Password)
    {
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{usuarioIngresado}:{DateTime.UtcNow:yyyyMMdd}"));
        return Results.Ok(new { ok = true, token, usuario = usuarioIngresado });
    }
    return Results.Json(new { ok = false, mensaje = "Usuario o contraseña incorrectos." }, statusCode: 401);
});
app.MapGet("/api/grados", async () =>
{
    var grados = new List<object>();
    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    string sql = @"
        SELECT grado, COUNT(1) AS matriculados
        FROM dbo.estudiantes
        WHERE LOWER(grado) LIKE 'bachillerato%'
           OR LOWER(grado) LIKE 'tecnico%'
           OR LOWER(grado) LIKE 'universidad%'
           OR LOWER(grado) LIKE 'posgrado%'
        GROUP BY grado
        ORDER BY grado ASC;";

    using var cmd = new SqlCommand(sql, conn);
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        string nombreGrado = reader.GetString(0);
        int matriculados = reader.GetInt32(1);
        int disponibles = Math.Max(0, 30 - matriculados);

        grados.Add(new
        {
            grado = nombreGrado,
            matriculados,
            disponibles,
            limite = 30
        });
    }
    return Results.Ok(grados);
});

app.MapGet("/api/estudiantes", async (string grado, string? fecha) =>
{
    DateTime fechaConsulta = DateTime.TryParse(fecha, out var f) ? f.Date : DateTime.UtcNow.AddHours(-6).Date;
    var lista = new List<object>();

    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    string sql = "SELECT e.id, e.codigo, e.nombre, e.grado, e.edad, ISNULL(a.estado, '') AS estadoFecha, ISNULL(a.observacion, '') AS observacionFecha FROM dbo.estudiantes e LEFT JOIN dbo.Asistencias a ON e.id = a.estudiante_id AND a.fecha = @fecha WHERE e.grado = @grado ORDER BY e.nombre ASC;";

    using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@grado", grado);
    cmd.Parameters.AddWithValue("@fecha", fechaConsulta);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        lista.Add(new
        {
            id = reader.GetInt32(0),
            codigo = reader.GetInt32(1),
            nombre = reader.GetString(2),
            grado = reader.GetString(3),
            edad = reader.GetInt32(4),
            estadoHoy = reader.GetString(5),
            observacionHoy = reader.GetString(6)
        });
    }
    return Results.Ok(lista);
});

app.MapPost("/api/estudiantes", async (NuevoEstudianteRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Nombre) || string.IsNullOrWhiteSpace(req.Grado) || req.Edad < 12 || req.Edad > 99)
        return Results.BadRequest(new { mensaje = "Complete el nombre, seleccione un grado válido e ingrese una edad entre 12 y 99 años." });

    string gradoBase = req.Grado.Trim().ToLower();
    if (!gradosPermitidos.TryGetValue(gradoBase, out int prefijoGrado))
        return Results.BadRequest(new { mensaje = "El grado seleccionado no pertenece a los niveles académicos establecidos." });

    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    // 1. Determinar sección disponible (máximo 30 estudiantes por sección)
    string gradoAsignado = "";
    int cupoActual = 0;
    for (int idx = 0; idx < 26; idx++)
    {
        char letraSeccion = (char)('A' + idx);
        string candidato = $"{gradoBase} - seccion {letraSeccion}".ToLower();

        // Para la Sección A, también contabiliza registros históricos que solo decían el nombre base sin sección
        string sqlCount = idx == 0
            ? "SELECT COUNT(1) FROM dbo.estudiantes WHERE LOWER(grado) = @candidato OR LOWER(grado) = @gradoBase"
            : "SELECT COUNT(1) FROM dbo.estudiantes WHERE LOWER(grado) = @candidato";

        using var countCmd = new SqlCommand(sqlCount, conn);
        countCmd.Parameters.AddWithValue("@candidato", candidato);
        countCmd.Parameters.AddWithValue("@gradoBase", gradoBase);
        int inscritos = (int)await countCmd.ExecuteScalarAsync();

        if (inscritos < 30)
        {
            gradoAsignado = candidato;
            cupoActual = inscritos + 1;
            break;
        }
    }

    if (string.IsNullOrEmpty(gradoAsignado))
        return Results.Conflict(new { mensaje = "Todas las secciones disponibles para este grado alcanzaron su cupo máximo." });

    // 2. Generar código automático según Grado (1 dígito) + Edad (2 dígitos) + Correlativo (3 dígitos)
    int baseCodigo = (prefijoGrado * 100000) + (req.Edad * 1000);
    int rangoMin = baseCodigo + 1;
    int rangoMax = baseCodigo + 999;

    int codigoGenerado = rangoMin;
    using (var codCmd = new SqlCommand("SELECT ISNULL(MAX(codigo), @baseCod) + 1 FROM dbo.estudiantes WHERE codigo BETWEEN @min AND @max", conn))
    {
        codCmd.Parameters.AddWithValue("@baseCod", baseCodigo);
        codCmd.Parameters.AddWithValue("@min", rangoMin);
        codCmd.Parameters.AddWithValue("@max", rangoMax);
        codigoGenerado = (int)await codCmd.ExecuteScalarAsync();
    }

    // 3. Generar ID interno correlativo
    int nuevoId = 1;
    using (var maxCmd = new SqlCommand("SELECT ISNULL(MAX(id), 0) + 1 FROM dbo.estudiantes", conn))
    {
        nuevoId = (int)await maxCmd.ExecuteScalarAsync();
    }

    string sqlInsert = "INSERT INTO dbo.estudiantes (id, codigo, nombre, grado, edad) VALUES (@id, @codigo, @nombre, @grado, @edad);";
    using var cmd = new SqlCommand(sqlInsert, conn);
    cmd.Parameters.AddWithValue("@id", nuevoId);
    cmd.Parameters.AddWithValue("@codigo", codigoGenerado);
    cmd.Parameters.AddWithValue("@nombre", req.Nombre.Trim());
    cmd.Parameters.AddWithValue("@grado", gradoAsignado);
    cmd.Parameters.AddWithValue("@edad", req.Edad);

    await cmd.ExecuteNonQueryAsync();

    string avisoCupo = cupoActual == 30
        ? " (Sección llena: los próximos registros pasarán a la siguiente sección)."
        : "";

    return Results.Ok(new
    {
        mensaje = $"Estudiante matriculado en {gradoAsignado.ToUpper()} (Cupo {cupoActual}/30). Código asignado: {codigoGenerado}.{avisoCupo}",
        id = nuevoId,
        codigo = codigoGenerado,
        grado = gradoAsignado
    });
});

app.MapPost("/api/asistencia", async (GuardarAsistenciaPayload payload) =>
{
    DateTime hoyElSalvador = DateTime.UtcNow.AddHours(-6).Date;
    DateTime fechaRegistro = DateTime.TryParse(payload.Fecha, out var f) ? f.Date : hoyElSalvador;

    if (fechaRegistro < hoyElSalvador)
    {
        return Results.BadRequest(new { mensaje = $"No se permite registrar ni modificar asistencias de días pasados ({fechaRegistro:dd/MM/yyyy}). Solo se admite desde hoy ({hoyElSalvador:dd/MM/yyyy})." });
    }

    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    string sql = "MERGE dbo.Asistencias AS target USING (SELECT @estudiante_id AS estudiante_id, @fecha AS fecha) AS source ON (target.estudiante_id = source.estudiante_id AND target.fecha = source.fecha) WHEN MATCHED THEN UPDATE SET estado = @estado, observacion = @observacion WHEN NOT MATCHED THEN INSERT (estudiante_id, fecha, estado, observacion) VALUES (@estudiante_id, source.fecha, @estado, @observacion);";

    foreach (var item in payload.Registros)
    {
        if (string.IsNullOrWhiteSpace(item.Estado)) continue;

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@estudiante_id", item.EstudianteId);
        cmd.Parameters.AddWithValue("@fecha", fechaRegistro);
        cmd.Parameters.AddWithValue("@estado", item.Estado);
        cmd.Parameters.AddWithValue("@observacion", string.IsNullOrWhiteSpace(item.Observacion) ? DBNull.Value : item.Observacion);
        await cmd.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { mensaje = $"Asistencia del {fechaRegistro:dd/MM/yyyy} guardada correctamente." });
});

app.MapPost("/api/reporte-mensual", async (ReporteRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.CorreoDestino))
        return Results.BadRequest(new { mensaje = "Ingresa un correo destino válido." });

    var partes = req.Mes.Split('-');
    int anio = int.Parse(partes[0]);
    int mes = int.Parse(partes[1]);

    using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();

    string sql = "SELECT e.codigo, e.nombre, e.grado, SUM(CASE WHEN a.estado = 'Presente' THEN 1 ELSE 0 END) AS presentes, SUM(CASE WHEN a.estado = 'Ausente' THEN 1 ELSE 0 END) AS ausentes, SUM(CASE WHEN a.estado = 'Permiso' THEN 1 ELSE 0 END) AS permisos, COUNT(a.id) AS totalRegistros FROM dbo.estudiantes e INNER JOIN dbo.Asistencias a ON e.id = a.estudiante_id WHERE YEAR(a.fecha) = @anio AND MONTH(a.fecha) = @mes GROUP BY e.codigo, e.nombre, e.grado ORDER BY ausentes DESC, e.nombre ASC;";

    using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@anio", anio);
    cmd.Parameters.AddWithValue("@mes", mes);

    var filas = new List<(int Codigo, string Nombre, string Grado, int Presentes, int Ausentes, int Permisos, int Total, double Porcentaje)>();

    using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            int pres = reader.GetInt32(3);
            int aus = reader.GetInt32(4);
            int perm = reader.GetInt32(5);
            int tot = reader.GetInt32(6);
            double pct = tot > 0 ? Math.Round((pres * 100.0) / tot, 2) : 0;

            filas.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), pres, aus, perm, tot, pct));
        }
    }

    if (filas.Count == 0)
        return Results.NotFound(new { mensaje = $"No hay registros de asistencia en {req.Mes}." });

    var masFalto = filas.OrderByDescending(x => x.Ausentes).First();
    double promedioGeneral = Math.Round(filas.Average(x => x.Porcentaje), 2);

    var csv = new StringBuilder();
    csv.AppendLine("Codigo;Estudiante;Grado;Presentes;Ausentes;Permisos;Total Dias;Porcentaje Asistencia (%)");
    foreach (var f in filas)
    {
        csv.AppendLine($"{f.Codigo};{f.Nombre};{f.Grado};{f.Presentes};{f.Ausentes};{f.Permisos};{f.Total};{f.Porcentaje}%");
    }
    csv.AppendLine();
    csv.AppendLine($"Promedio General del Mes;{promedioGeneral}%");
    csv.AppendLine($"Estudiante con mas faltas;{masFalto.Nombre} ({masFalto.Ausentes} ausencias)");

    byte[] csvBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();

    try
    {
        using var http = new HttpClient();
        string cuerpoTexto = $"Resumen de Asistencia ({req.Mes}):\n\n- Total de estudiantes evaluados: {filas.Count}\n- Porcentaje promedio de asistencia: {promedioGeneral}%\n- Estudiante con mayor cantidad de ausencias: {masFalto.Nombre} (Código {masFalto.Codigo} - {masFalto.Grado}) con {masFalto.Ausentes} falta(s).\n\nSe adjunta el documento detallado en formato CSV (compatible con Microsoft Excel).";

        var payloadCorreo = new
        {
            to = req.CorreoDestino.Trim(),
            subject = $"Reporte Mensual de Asistencia - {req.Mes}",
            text = cuerpoTexto,
            filename = $"Reporte_Asistencia_{req.Mes}.csv",
            csvBase64 = Convert.ToBase64String(csvBytes)
        };

        string json = JsonSerializer.Serialize(payloadCorreo);
        using var content = new StringContent(json, Encoding.UTF8, "text/plain");

        var response = await http.PostAsync(googleScriptUrl, content);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode || body.TrimStart().StartsWith("<"))
        {
            return Results.Problem($"Error al enviar: {body.Substring(0, Math.Min(200, body.Length))}");
        }

        return Results.Ok(new { mensaje = $"Reporte enviado a {req.CorreoDestino}. Mayor ausentismo: {masFalto.Nombre} ({masFalto.Ausentes} faltas)." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error al enviar el correo: {ex.Message}");
    }
});

app.Run();

record LoginRequest(string Usuario, string Password);
record AsistenciaRequest(int EstudianteId, string Estado, string? Observacion);
record GuardarAsistenciaPayload(string Fecha, List<AsistenciaRequest> Registros);
record NuevoEstudianteRequest(string Nombre, string Grado, int Edad);
record ReporteRequest(string Mes, string CorreoDestino);